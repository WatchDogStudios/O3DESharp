/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace O3DESharp.BindingGenerator.Caching
{
    /// <summary>
    /// Represents a cached entry for a gem's binding generation
    /// </summary>
    public class GemCacheEntry
    {
        /// <summary>
        /// Gem name
        /// </summary>
        [JsonPropertyName("gem_name")]
        public string GemName { get; set; } = string.Empty;

        /// <summary>
        /// Combined hash of all input header files
        /// </summary>
        [JsonPropertyName("input_hash")]
        public string InputHash { get; set; } = string.Empty;

        /// <summary>
        /// Hash of the binding configuration
        /// </summary>
        [JsonPropertyName("config_hash")]
        public string ConfigHash { get; set; } = string.Empty;

        /// <summary>
        /// Hash of the binding generator tool version
        /// </summary>
        [JsonPropertyName("tool_version")]
        public string ToolVersion { get; set; } = string.Empty;

        /// <summary>
        /// Individual file hashes for dependency tracking. Includes both the
        /// direct gem headers and every transitively #included non-system
        /// header reported by libclang during the previous parse.
        /// </summary>
        [JsonPropertyName("file_hashes")]
        public Dictionary<string, string> FileHashes { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Fully-qualified paths of the direct header files the generator was
        /// invoked with for this gem. Used by <see cref="BuildCache.NeedsRegeneration"/>
        /// to detect when the user-visible input set changes. Subset of
        /// <see cref="FileHashes"/>; older cache files written before this
        /// field existed leave it empty and the cache treats them as a miss.
        /// </summary>
        [JsonPropertyName("direct_headers")]
        public List<string> DirectHeaders { get; set; } = new List<string>();

        /// <summary>
        /// List of generated output files
        /// </summary>
        [JsonPropertyName("output_files")]
        public List<string> OutputFiles { get; set; } = new List<string>();

        /// <summary>
        /// Timestamp of last successful generation
        /// </summary>
        [JsonPropertyName("last_generated")]
        public DateTime LastGenerated { get; set; }
    }

    /// <summary>
    /// Root cache structure storing all gem cache entries
    /// </summary>
    public class BuildCacheData
    {
        /// <summary>
        /// Cache format version for compatibility checking
        /// </summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        /// <summary>
        /// Cached gem entries indexed by gem name
        /// </summary>
        [JsonPropertyName("gems")]
        public Dictionary<string, GemCacheEntry> Gems { get; set; } = new Dictionary<string, GemCacheEntry>();
    }

    /// <summary>
    /// Manages build cache for incremental builds
    /// </summary>
    public class BuildCache
    {
        private readonly string _cacheFilePath;
        private readonly bool _verbose;
        private BuildCacheData _cacheData;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Current tool version for cache invalidation
        /// </summary>
        public static string ToolVersion => "1.0.0";

        public BuildCache(string cacheFilePath, bool verbose = false)
        {
            _cacheFilePath = cacheFilePath;
            _verbose = verbose;
            _cacheData = Load();
        }

        /// <summary>
        /// Check if a gem needs regeneration
        /// </summary>
        /// <param name="gemName">Name of the gem</param>
        /// <param name="headerFiles">Header files to check</param>
        /// <param name="configHash">Hash of current configuration</param>
        /// <returns>True if regeneration is needed</returns>
        public bool NeedsRegeneration(string gemName, IEnumerable<string> headerFiles, string configHash)
        {
            if (!_cacheData.Gems.TryGetValue(gemName, out var entry))
            {
                Log($"Cache miss for gem '{gemName}': No cache entry exists");
                return true;
            }

            // Check tool version
            if (entry.ToolVersion != ToolVersion)
            {
                Log($"Cache miss for gem '{gemName}': Tool version changed ({entry.ToolVersion} -> {ToolVersion})");
                return true;
            }

            // Check config hash
            if (entry.ConfigHash != configHash)
            {
                Log($"Cache miss for gem '{gemName}': Configuration changed");
                return true;
            }

            // Check if any output files are missing
            foreach (var outputFile in entry.OutputFiles)
            {
                if (!File.Exists(outputFile))
                {
                    Log($"Cache miss for gem '{gemName}': Output file missing: {outputFile}");
                    return true;
                }
            }

            // Compare the user-visible direct header set. Transitive includes
            // (everything else in FileHashes) are content-checked below; we
            // don't compare them as a set because libclang may legitimately
            // discover a different transitive set on a re-parse if upstream
            // headers were edited.
            var currentDirect = headerFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cachedDirect = entry.DirectHeaders.Count > 0
                ? entry.DirectHeaders.ToHashSet(StringComparer.OrdinalIgnoreCase)
                // Cache predates DirectHeaders: treat as miss so the next
                // successful build populates it correctly.
                : null;
            if (cachedDirect == null)
            {
                Log($"Cache miss for gem '{gemName}': Cache predates direct-headers tracking");
                return true;
            }
            if (!cachedDirect.SetEquals(currentDirect))
            {
                Log($"Cache miss for gem '{gemName}': Direct header set changed");
                return true;
            }

            // Hash-check every file we know about — direct AND transitive. A
            // change in an upstream AzCore header now invalidates the cache
            // even though the gem's own headers are byte-identical.
            foreach (var kvp in entry.FileHashes)
            {
                var fullPath = kvp.Key;
                var cachedHash = kvp.Value;

                if (!File.Exists(fullPath))
                {
                    Log($"Cache miss for gem '{gemName}': File no longer exists: {fullPath}");
                    return true;
                }

                try
                {
                    var currentHash = FileHasher.ComputeFileHash(fullPath);
                    if (currentHash != cachedHash)
                    {
                        Log($"Cache miss for gem '{gemName}': File changed: {fullPath}");
                        return true;
                    }
                }
                catch
                {
                    Log($"Cache miss for gem '{gemName}': Cannot read file: {fullPath}");
                    return true;
                }
            }

            Log($"Cache hit for gem '{gemName}': No changes detected (incl. {entry.FileHashes.Count - entry.DirectHeaders.Count} transitive header(s))");
            return false;
        }

        /// <summary>
        /// Update cache entry after successful generation
        /// </summary>
        /// <param name="gemName">Name of the gem</param>
        /// <param name="headerFiles">Input header files</param>
        /// <param name="configHash">Configuration hash</param>
        /// <param name="outputFiles">Generated output files</param>
        public void UpdateEntry(string gemName, IEnumerable<string> headerFiles, string configHash, IEnumerable<string> outputFiles)
        {
            // Legacy overload - no transitive info, used by callers that only
            // know the direct headers. Prefer the overload below.
            UpdateEntry(gemName, headerFiles, Array.Empty<string>(), configHash, outputFiles);
        }

        /// <summary>
        /// Update cache entry recording both the direct headers the generator
        /// was invoked with and the transitive headers libclang reported during
        /// parsing. The transitive set lets us invalidate when an upstream
        /// header changes even though the gem's own headers did not.
        /// </summary>
        public void UpdateEntry(
            string gemName,
            IEnumerable<string> directHeaders,
            IEnumerable<string> transitiveHeaders,
            string configHash,
            IEnumerable<string> outputFiles)
        {
            var entry = new GemCacheEntry
            {
                GemName = gemName,
                ConfigHash = configHash,
                ToolVersion = ToolVersion,
                LastGenerated = DateTime.UtcNow,
                OutputFiles = outputFiles.ToList()
            };

            // Record direct headers separately for the NeedsRegeneration set-comparison.
            foreach (var file in directHeaders)
            {
                try
                {
                    var fullPath = Path.GetFullPath(file);
                    entry.DirectHeaders.Add(fullPath);
                    entry.FileHashes[fullPath] = FileHasher.ComputeFileHash(file);
                }
                catch (Exception ex)
                {
                    Log($"Warning: Could not hash file {file}: {ex.Message}");
                }
            }

            // Add transitive (#included) headers to FileHashes only - they're not
            // part of the "input set" the user passed.
            foreach (var file in transitiveHeaders)
            {
                try
                {
                    var fullPath = Path.GetFullPath(file);
                    if (entry.FileHashes.ContainsKey(fullPath))
                    {
                        continue; // already hashed as a direct header
                    }
                    if (!File.Exists(fullPath))
                    {
                        continue; // ignore files clang reported that aren't on disk
                    }
                    entry.FileHashes[fullPath] = FileHasher.ComputeFileHash(fullPath);
                }
                catch (Exception ex)
                {
                    Log($"Warning: Could not hash transitive file {file}: {ex.Message}");
                }
            }

            // Compute combined input hash
            entry.InputHash = FileHasher.ComputeStringHash(string.Join("|", entry.FileHashes.Values));

            _cacheData.Gems[gemName] = entry;
            Log($"Updated cache entry for gem '{gemName}': {entry.DirectHeaders.Count} direct + {entry.FileHashes.Count - entry.DirectHeaders.Count} transitive headers");
        }

        /// <summary>
        /// Remove cache entry for a gem
        /// </summary>
        /// <param name="gemName">Name of the gem</param>
        public void InvalidateEntry(string gemName)
        {
            if (_cacheData.Gems.Remove(gemName))
            {
                Log($"Invalidated cache entry for gem '{gemName}'");
            }
        }

        /// <summary>
        /// Clear entire cache
        /// </summary>
        public void Clear()
        {
            _cacheData = new BuildCacheData();
            Log("Cleared all cache entries");
        }

        /// <summary>
        /// Save cache to disk
        /// </summary>
        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(_cacheData, JsonOptions);
                File.WriteAllText(_cacheFilePath, json);
                Log($"Saved cache to {_cacheFilePath}");
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not save cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Get statistics about the cache
        /// </summary>
        public (int EntryCount, long TotalFilesTracked) GetStats()
        {
            var totalFiles = _cacheData.Gems.Values.Sum(e => e.FileHashes.Count);
            return (_cacheData.Gems.Count, totalFiles);
        }

        private BuildCacheData Load()
        {
            if (!File.Exists(_cacheFilePath))
            {
                Log($"No cache file found at {_cacheFilePath}, starting fresh");
                return new BuildCacheData();
            }

            try
            {
                var json = File.ReadAllText(_cacheFilePath);
                var data = JsonSerializer.Deserialize<BuildCacheData>(json, JsonOptions);

                if (data == null || data.Version != 1)
                {
                    Log("Cache version mismatch, starting fresh");
                    return new BuildCacheData();
                }

                Log($"Loaded cache with {data.Gems.Count} entries");
                return data;
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not load cache: {ex.Message}");
                return new BuildCacheData();
            }
        }

        private void Log(string message)
        {
            if (_verbose)
            {
                Console.WriteLine($"[Cache] {message}");
            }
        }
    }
}
