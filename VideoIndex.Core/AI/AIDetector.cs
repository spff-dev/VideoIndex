using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace VideoIndex.Core.AI
{
    /// <summary>
    /// Local AI detector for content analysis
    /// Integrates with Python-based AI models for performer counting and watermark detection
    /// </summary>
    public class AIDetector
    {
        private readonly string _pythonPath;
        private readonly string _scriptPath;
        private readonly bool _isEnabled;

        public AIDetector(string pythonPath = "python", string scriptPath = "AI/ai_detector.py", bool isEnabled = true)
        {
            _pythonPath = pythonPath;
            _scriptPath = scriptPath;
            _isEnabled = isEnabled;
        }

        /// <summary>
        /// Analyzes a video file using local AI models
        /// </summary>
        /// <param name="videoPath">Absolute path to video file</param>
        /// <returns>Analysis results or null if disabled/failed</returns>
        public async Task<AIAnalysisResult?> AnalyzeVideoAsync(string videoPath)
        {
            if (!_isEnabled)
                return null;

            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                return null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _pythonPath,
                    Arguments = $"\"{_scriptPath}\" \"{videoPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.GetCurrentDirectory()
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                // Read output
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                // Wait for completion (with timeout)
                var completedInTime = await Task.Run(() => process.WaitForExit(60000)); // 60 second timeout

                if (!completedInTime)
                {
                    try { process.Kill(true); } catch { }
                    Console.WriteLine($"AI Detection timed out for: {Path.GetFileName(videoPath)}");
                    return null;
                }

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"AI Detection failed (exit code {process.ExitCode}): {error}");
                    return null;
                }

                // Parse JSON result
                var result = JsonSerializer.Deserialize<AIAnalysisResult>(output, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AI Detection exception for {Path.GetFileName(videoPath)}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Check if AI detection is available (Python + dependencies installed)
        /// </summary>
        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _pythonPath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();
                await process.WaitForExitAsync();

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Result from AI analysis
    /// </summary>
    public record AIAnalysisResult
    {
        public bool Success { get; init; }
        public int? PerformerCount { get; init; }
        public bool OnlyFansDetected { get; init; }
        public string? OnlyFansUsername { get; init; }
        public double Confidence { get; init; }
        public string? Error { get; init; }
    }
}
