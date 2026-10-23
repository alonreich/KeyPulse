using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace KeyPulse
{
    /// <summary>
    /// Reliable atomic file writer that guarantees:
    /// 1. Data is written to a unique temporary file on the destination directory.
    /// 2. Content is flushed to disk before promotion.
    /// 3. Destination is replaced atomically using File.Replace or File.Move.
    /// 4. Never degrades to an unsafe direct overwrite (File.Copy(..., true)),
    ///    preserving the existing valid destination file if replacement cannot be performed.
    /// </summary>
    public static class AtomicFileWriter
    {
        public static bool WriteAtomic(string targetPath, string content, out string error)
        {
            return WriteAtomic(targetPath, Encoding.UTF8.GetBytes(content), out error);
        }

        public static bool WriteAtomic(string targetPath, byte[] bytes, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                error = "Target path cannot be empty.";
                return false;
            }

            var dir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(dir))
            {
                dir = AppContext.BaseDirectory;
            }

            string tempPath = string.Empty;
            try
            {
                Directory.CreateDirectory(dir);
                tempPath = Path.Combine(dir, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }

                var tempInfo = new FileInfo(tempPath);
                if (!tempInfo.Exists || tempInfo.Length != bytes.Length)
                {
                    error = "Temporary file write verification failed.";
                    TryDelete(tempPath);
                    return false;
                }

                if (File.Exists(targetPath))
                {
                    bool replaced = false;
                    Exception? lastException = null;

                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            File.Replace(tempPath, targetPath, null, true);
                            replaced = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                            Thread.Sleep(50);
                        }
                    }

                    if (!replaced)
                    {
                        TryDelete(tempPath);
                        error = $"Failed to atomically replace destination file '{targetPath}': {lastException?.Message}";
                        Program.LogCrash($"Atomic write failed (replace): {error}");
                        return false;
                    }
                }
                else
                {
                    try
                    {
                        File.Move(tempPath, targetPath);
                    }
                    catch (Exception moveEx)
                    {
                        if (File.Exists(targetPath))
                        {
                            try
                            {
                                File.Replace(tempPath, targetPath, null, true);
                            }
                            catch (Exception replaceEx)
                            {
                                TryDelete(tempPath);
                                error = $"Failed to move/replace file into '{targetPath}': {replaceEx.Message}";
                                Program.LogCrash($"Atomic write failed (move/replace): {error}");
                                return false;
                            }
                        }
                        else
                        {
                            TryDelete(tempPath);
                            error = $"Failed to move file to '{targetPath}': {moveEx.Message}";
                            Program.LogCrash($"Atomic write failed (move): {error}");
                            return false;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Atomic file write failed for '{targetPath}': {ex.Message}";
                Program.LogCrash(error);
                return false;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        public static bool TryVerifyReadableJson<T>(string filePath, JsonTypeInfo<T> jsonTypeInfo, out T? result, out string error)
        {
            result = default;
            error = string.Empty;

            try
            {
                if (!File.Exists(filePath))
                {
                    error = "File does not exist.";
                    return false;
                }

                var text = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(text))
                {
                    error = "File is empty.";
                    return false;
                }

                result = JsonSerializer.Deserialize(text, jsonTypeInfo);
                if (result == null)
                {
                    error = "Deserialization produced null.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}
