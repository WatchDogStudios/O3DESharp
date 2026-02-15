/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace O3DESharp.BindingGenerator.Caching
{
    /// <summary>
    /// Computes SHA256 hashes for files to detect changes
    /// </summary>
    public static class FileHasher
    {
        /// <summary>
        /// Compute SHA256 hash of a file's contents
        /// </summary>
        /// <param name="filePath">Path to the file</param>
        /// <returns>Hex-encoded SHA256 hash</returns>
        public static string ComputeFileHash(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Cannot hash non-existent file: {filePath}");
            }

            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha256.ComputeHash(stream);
            return BytesToHex(hashBytes);
        }

        /// <summary>
        /// Compute SHA256 hash of string content
        /// </summary>
        /// <param name="content">String content to hash</param>
        /// <returns>Hex-encoded SHA256 hash</returns>
        public static string ComputeStringHash(string content)
        {
            using var sha256 = SHA256.Create();
            var contentBytes = Encoding.UTF8.GetBytes(content);
            var hashBytes = sha256.ComputeHash(contentBytes);
            return BytesToHex(hashBytes);
        }

        /// <summary>
        /// Compute combined hash of multiple files
        /// </summary>
        /// <param name="filePaths">Paths to files</param>
        /// <returns>Combined hex-encoded SHA256 hash</returns>
        public static string ComputeCombinedHash(params string[] filePaths)
        {
            using var sha256 = SHA256.Create();
            var combined = new StringBuilder();

            foreach (var path in filePaths)
            {
                if (File.Exists(path))
                {
                    combined.Append(ComputeFileHash(path));
                }
            }

            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined.ToString()));
            return BytesToHex(hashBytes);
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
