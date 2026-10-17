using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace KeyPulse
{
    public class IpcMessage
    {
        public string Command { get; set; } = string.Empty;
        public string? Target { get; set; }
    }

    public class IpcResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Reliable Named Pipe IPC channel replacing the single shared file handoff (staged-add.txt).
    /// Supports multi-process requests, framing with explicit length limits, per-request ACK,
    /// FIFO dispatch, and cross-integrity access in the same Windows session.
    /// </summary>
    public static class IpcService
    {
        public static string PipeSimpleName => Program.IsDevMode
            ? $"KeyPulse_IPC_DEV_Session_{Process.GetCurrentProcess().SessionId}"
            : $"KeyPulse_IPC_Session_{Process.GetCurrentProcess().SessionId}";

        public static string PipeName => $@"\\.\pipe\{PipeSimpleName}";

        private const string SharedObjectSddl = "D:(A;;GA;;;WD)S:(ML;;NW;;;LW)";
        private const uint SecurityDescriptorRevision = 1;
        private const int MaxMessageBytes = 8192;

        private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint PIPE_TYPE_BYTE = 0x00000000;
        private const uint PIPE_READMODE_BYTE = 0x00000000;
        private const uint PIPE_WAIT = 0x00000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateNamedPipeW(
            string lpName, uint dwOpenMode, uint dwPipeMode, uint nMaxInstances,
            uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut, IntPtr lpSecurityAttributes);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string stringSecurityDescriptor, uint stringSDRevision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        private static CancellationTokenSource? _serverCts;
        private static Task? _serverTask;

        public static void StartServer(Action<string> onAddTarget)
        {
            StopServer();
            _serverCts = new CancellationTokenSource();
            var token = _serverCts.Token;

            _serverTask = Task.Run(async () =>
            {
                Program.LogDebug($"IPC server listening on {PipeName}");
                while (!token.IsCancellationRequested)
                {
                    NamedPipeServerStream? server = null;
                    try
                    {
                        server = CreateServerPipe();
                        if (server == null)
                        {
                            await Task.Delay(1000, token);
                            continue;
                        }

                        await server.WaitForConnectionAsync(token);

                        var msg = await ReadMessageAsync(server, token);
                        if (msg != null && msg.Command == "AddTarget" && !string.IsNullOrWhiteSpace(msg.Target))
                        {
                            Program.LogDebug($"IPC received AddTarget: {msg.Target}");
                            await SendResponseAsync(server, new IpcResponse { Success = true, Message = "Accepted" }, token);
                            try
                            {
                                onAddTarget(msg.Target);
                            }
                            catch (Exception ex)
                            {
                                Program.LogCrash($"IPC onAddTarget error: {ex.Message}");
                            }
                        }
                        else
                        {
                            await SendResponseAsync(server, new IpcResponse { Success = false, Message = "Invalid message" }, token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug($"IPC server loop exception: {ex.Message}");
                        await Task.Delay(200, token);
                    }
                    finally
                    {
                        if (server != null)
                        {
                            try
                            {
                                if (server.IsConnected) server.Disconnect();
                            }
                            catch { }
                            server.Dispose();
                        }
                    }
                }
            }, token);
        }

        public static void StopServer()
        {
            try
            {
                _serverCts?.Cancel();
                _serverCts?.Dispose();
                _serverCts = null;
            }
            catch { }
        }

        public static bool TrySendAddTarget(string target, int timeoutMs = 3000)
        {
            try
            {
                var pipeNameOnly = PipeSimpleName;
                using var client = new NamedPipeClientStream(".", pipeNameOnly, PipeDirection.InOut);
                client.Connect(timeoutMs);

                var msg = new IpcMessage { Command = "AddTarget", Target = target };
                var json = JsonSerializer.Serialize(msg, AppConfigJsonContext.Default.IpcMessage);
                var bytes = Encoding.UTF8.GetBytes(json);

                var lengthBytes = BitConverter.GetBytes(bytes.Length);
                client.Write(lengthBytes, 0, 4);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();

                var respLenBytes = new byte[4];
                int read = client.Read(respLenBytes, 0, 4);
                if (read == 4)
                {
                    int respLen = BitConverter.ToInt32(respLenBytes, 0);
                    if (respLen > 0 && respLen <= MaxMessageBytes)
                    {
                        var respBytes = new byte[respLen];
                        int totalRead = 0;
                        while (totalRead < respLen)
                        {
                            int r = client.Read(respBytes, totalRead, respLen - totalRead);
                            if (r <= 0) break;
                            totalRead += r;
                        }
                        return totalRead == respLen;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Program.LogDebug($"IPC TrySendAddTarget failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<IpcMessage?> ReadMessageAsync(NamedPipeServerStream stream, CancellationToken token)
        {
            var lenBytes = new byte[4];
            int read = await stream.ReadAsync(lenBytes, 0, 4, token);
            if (read < 4) return null;

            int len = BitConverter.ToInt32(lenBytes, 0);
            if (len <= 0 || len > MaxMessageBytes) return null;

            var buffer = new byte[len];
            int totalRead = 0;
            while (totalRead < len)
            {
                int r = await stream.ReadAsync(buffer, totalRead, len - totalRead, token);
                if (r <= 0) break;
                totalRead += r;
            }

            if (totalRead != len) return null;
            var json = Encoding.UTF8.GetString(buffer, 0, len);
            return JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.IpcMessage);
        }

        private static async Task SendResponseAsync(NamedPipeServerStream stream, IpcResponse response, CancellationToken token)
        {
            var json = JsonSerializer.Serialize(response, AppConfigJsonContext.Default.IpcResponse);
            var bytes = Encoding.UTF8.GetBytes(json);
            var lenBytes = BitConverter.GetBytes(bytes.Length);
            await stream.WriteAsync(lenBytes, 0, 4, token);
            await stream.WriteAsync(bytes, 0, bytes.Length, token);
            await stream.FlushAsync(token);
        }

        private static NamedPipeServerStream? CreateServerPipe()
        {
            IntPtr descriptor = IntPtr.Zero;
            try
            {
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(SharedObjectSddl, SecurityDescriptorRevision, out descriptor, IntPtr.Zero))
                {
                    return new NamedPipeServerStream(
                        PipeSimpleName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }

                var attributes = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = descriptor,
                    bInheritHandle = 0
                };

                IntPtr attrPtr = Marshal.AllocHGlobal(attributes.nLength);
                try
                {
                    Marshal.StructureToPtr(attributes, attrPtr, false);
                    IntPtr handle = CreateNamedPipeW(
                        PipeName,
                        PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                        255,
                        MaxMessageBytes,
                        MaxMessageBytes,
                        5000,
                        attrPtr);

                    if (handle == (IntPtr)(-1))
                    {
                        Program.LogDebug($"CreateNamedPipeW failed: {Marshal.GetLastWin32Error()}");
                        return null;
                    }

                    var safeHandle = new SafePipeHandle(handle, true);
                    return new NamedPipeServerStream(PipeDirection.InOut, true, false, safeHandle);
                }
                finally
                {
                    Marshal.FreeHGlobal(attrPtr);
                }
            }
            finally
            {
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            }
        }
    }
}
