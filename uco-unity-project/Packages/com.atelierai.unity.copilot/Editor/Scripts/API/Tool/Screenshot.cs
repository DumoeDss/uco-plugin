/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using Microsoft.Win32.SafeHandles;
using UnityEditor;
using UnityEngine;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace AIGD
{
    [Description("Bounded metadata for a PNG screenshot written to disk or returned without base64.")]
    public class ScreenshotOutputMetadata
    {
        public bool Ok { get; set; }
        public string? Path { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string MimeType { get; set; } = "image/png";
        public long ByteCount { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public bool MetadataOnly { get; set; }
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    [UcoToolType]
    public partial class Tool_Screenshot
    {
        private const int MaxDimension = 16384;
#if UNITY_EDITOR_WIN
        const uint GenericRead = 0x80000000;
        const uint GenericWrite = 0x40000000;
        const uint FileReadAttributes = 0x80;
        const uint FileListDirectory = 0x1;
        const uint Synchronize = 0x00100000;
        const uint OpenExisting = 3;
        const uint FileFlagBackupSemantics = 0x02000000;
        const uint FileNameNormalized = 0;
        const uint NtFileOpen = 1;
        const uint NtFileCreate = 2;
        const uint NtFileDirectoryFile = 0x00000001;
        const uint NtFileSynchronousIoNonAlert = 0x00000020;
        const uint NtFileNonDirectoryFile = 0x00000040;
        const uint NtFileOpenReparsePoint = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ByHandleFileInformation
        {
            public FileAttributes FileAttributes;
            public FileTime CreationTime;
            public FileTime LastAccessTime;
            public FileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoStatusBlock
        {
            public IntPtr Status;
            public UIntPtr Information;
        }

        sealed class NativeObjectName : IDisposable
        {
            readonly IntPtr _buffer;
            internal IntPtr UnicodeStringPointer { get; }

            internal NativeObjectName(string value)
            {
                _buffer = Marshal.StringToHGlobalUni(value);
                var byteLength = checked((ushort)(value.Length * sizeof(char)));
                var unicodeString = new UnicodeString
                {
                    Length = byteLength,
                    MaximumLength = checked((ushort)(byteLength + sizeof(char))),
                    Buffer = _buffer
                };
                UnicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(unicodeString, UnicodeStringPointer, false);
            }

            public void Dispose()
            {
                Marshal.FreeHGlobal(UnicodeStringPointer);
                Marshal.FreeHGlobal(_buffer);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        [DllImport("ntdll.dll")]
        static extern int NtCreateFile(
            out SafeFileHandle fileHandle,
            uint desiredAccess,
            ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock,
            IntPtr allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            IntPtr eaBuffer,
            uint eaLength);

        [DllImport("ntdll.dll")]
        static extern uint RtlNtStatusToDosError(int status);
#endif

        private static readonly System.Text.Json.JsonSerializerOptions OperationJsonOptions = new()
        {
            IncludeFields = true
        };

        internal static ResponseCallTool QueueScreenshot(
            string kind,
            Func<ResponseCallTool> capture)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));

            return MainThread.Instance.Run(() =>
            {
                var operation = EditorOperationRegistry.Create(kind, "scheduled");
                EditorOperationRegistry.Schedule(() => RunQueuedScreenshot(operation.OperationId, capture));
                return ResponseCallTool.SuccessStructured(
                    JsonSerializer.SerializeToNode(operation, OperationJsonOptions));
            });
        }

        internal static void RunQueuedScreenshot(string operationId, Func<ResponseCallTool> capture)
        {
            var operation = EditorOperationRegistry.Get(operationId);
            if (operation == null || operation.IsTerminal) return;
            if (operation.CancellationRequested)
            {
                EditorOperationRegistry.CancelRunning(operationId, "cancelled-before-capture");
                return;
            }

            EditorOperationRegistry.Start(operationId, "capturing-non-interruptible");
            try
            {
                var response = capture();
                var resultJson = response.StructuredContent?.ToJsonString()
                    ?? JsonSerializer.Serialize(new
                    {
                        status = response.Status.ToString(),
                        message = response.GetMessage()
                    });

                if (EditorOperationRegistry.IsCancellationRequested(operationId))
                {
                    EditorOperationRegistry.CancelRunning(operationId,
                        "cancelled-after-non-interruptible-capture", resultJson);
                }
                else if (response.Status == ResponseStatus.Error)
                {
                    EditorOperationRegistry.Fail(operationId, "screenshot-capture-failed",
                        response.GetMessage() ?? "Screenshot capture failed.", "capture-failed", resultJson);
                }
                else
                {
                    EditorOperationRegistry.Succeed(operationId, resultJson);
                }
            }
            catch (Exception ex)
            {
                EditorOperationRegistry.Fail(operationId, "screenshot-capture-exception",
                    ex.GetBaseException().Message, "capture-failed");
            }
        }

        internal static ResponseCallTool BuildScreenshotResponse(
            byte[] pngBytes,
            int width,
            int height,
            string caption,
            string? outputFile,
            bool metadataOnly)
        {
            if (string.IsNullOrWhiteSpace(outputFile) && !metadataOnly)
                return ResponseCallTool.Image(pngBytes,
                    com.AtelierAI.Uco.Framework.Common.Consts.MimeType.ImagePng, caption);

            string? resolvedPath = null;
            if (!string.IsNullOrWhiteSpace(outputFile))
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                if (!TryWriteContainedOutputFile(projectRoot, outputFile!, pngBytes,
                        out resolvedPath, out var pathError))
                    return ResponseCallTool.Error(pathError);
            }

            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(pngBytes)).Replace("-", "").ToLowerInvariant();

            var metadata = new AIGD.ScreenshotOutputMetadata
            {
                Ok = true,
                Path = resolvedPath,
                Width = width,
                Height = height,
                ByteCount = pngBytes.LongLength,
                Sha256 = hash,
                MetadataOnly = metadataOnly
            };
            return ResponseCallTool.SuccessStructured(JsonSerializer.SerializeToNode(metadata));
        }

        internal static bool TryResolveContainedOutputPath(
            string projectRoot,
            string outputFile,
            out string resolvedPath,
            out string error)
            => TryResolveContainedOutputPath(projectRoot, outputFile, ReadAttributesIfExists,
                out resolvedPath, out error);

        internal static bool TryResolveContainedOutputPath(
            string projectRoot,
            string outputFile,
            Func<string, FileAttributes?> attributeReader,
            out string resolvedPath,
            out string error)
        {
            resolvedPath = string.Empty;
            error = string.Empty;
            try
            {
                var canonicalRoot = Path.GetFullPath(projectRoot);
                resolvedPath = Path.GetFullPath(Path.IsPathRooted(outputFile)
                    ? outputFile
                    : Path.Combine(canonicalRoot, outputFile));
                var comparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                var projectVolume = Path.GetPathRoot(canonicalRoot);
                var outputVolume = Path.GetPathRoot(resolvedPath);
                if (string.IsNullOrEmpty(projectVolume) || string.IsNullOrEmpty(outputVolume)
                    || !string.Equals(projectVolume, outputVolume, comparison))
                {
                    error = "outputFile must be on the same filesystem root as the Unity project.";
                    return false;
                }

                var rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), comparison)
                    || canonicalRoot.EndsWith(Path.AltDirectorySeparatorChar.ToString(), comparison)
                    ? canonicalRoot
                    : canonicalRoot + Path.DirectorySeparatorChar;
                if (!string.Equals(resolvedPath, canonicalRoot, comparison)
                    && !resolvedPath.StartsWith(rootPrefix, comparison))
                {
                    error = "outputFile must resolve inside the Unity project root.";
                    return false;
                }

                if (string.Equals(resolvedPath, canonicalRoot, comparison))
                    return true;

                var relative = resolvedPath.Substring(rootPrefix.Length);
                var current = canonicalRoot;
                foreach (var segment in relative.Split(
                             new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, segment);
                    var attributes = attributeReader(current);
                    if (attributes.HasValue && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "outputFile must not traverse a reparse point or junction inside the Unity project.";
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "outputFile could not be safely resolved: " + ex.GetBaseException().Message;
                return false;
            }
        }

        static FileAttributes? ReadAttributesIfExists(string path)
        {
            try
            {
                return File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        internal static bool TryWriteContainedOutputFile(
            string projectRoot,
            string outputFile,
            byte[] bytes,
            out string resolvedPath,
            out string error)
            => TryWriteContainedOutputFile(projectRoot, outputFile, bytes, null,
                out resolvedPath, out error);

        internal static bool TryWriteContainedOutputFile(
            string projectRoot,
            string outputFile,
            byte[] bytes,
            Action? afterDirectoriesLocked,
            out string resolvedPath,
            out string error)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (!TryResolveContainedOutputPath(projectRoot, outputFile,
                    out resolvedPath, out error))
                return false;
            if (!string.Equals(Path.GetExtension(resolvedPath), ".png",
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "outputFile must end with '.png'.";
                return false;
            }

#if UNITY_EDITOR_WIN
            return TryWriteContainedOutputFileWindows(projectRoot, resolvedPath, bytes,
                afterDirectoriesLocked, out resolvedPath, out error);
#else
            // System.IO does not expose a portable descriptor-relative, no-follow create.
            // Keep outputFile disabled rather than reintroduce a check-then-open symlink race.
            try
            {
                afterDirectoriesLocked?.Invoke();
            }
            catch (Exception ex)
            {
                error = "outputFile safety validation hook failed: "
                    + ex.GetBaseException().Message;
                return false;
            }
            error = "Secure screenshot output-file writes are unavailable on this platform.";
            return false;
#endif
        }

#if UNITY_EDITOR_WIN
        static bool TryWriteContainedOutputFileWindows(
            string projectRoot,
            string lexicalOutputPath,
            byte[] bytes,
            Action? afterDirectoriesLocked,
            out string resolvedPath,
            out string error)
        {
            resolvedPath = lexicalOutputPath;
            error = string.Empty;
            var directoryHandles = new List<SafeFileHandle>();
            try
            {
                var canonicalRoot = Path.GetFullPath(projectRoot);
                var comparison = StringComparison.OrdinalIgnoreCase;
                var rootPrefix = EnsureDirectorySuffix(canonicalRoot);
                var relative = lexicalOutputPath.Substring(rootPrefix.Length);
                var segments = relative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    throw new IOException("The output path has no file name.");

                var rootHandle = OpenRootDirectoryHandle(canonicalRoot);
                directoryHandles.Add(rootHandle);
                var rootInformation = GetHandleInformation(rootHandle, canonicalRoot);
                if ((rootInformation.FileAttributes & FileAttributes.Directory) == 0)
                    throw new IOException("The Unity project root is not a directory.");
                var lockedRoot = GetFinalPath(rootHandle);
                var lockedRootPrefix = EnsureDirectorySuffix(lockedRoot);

                for (var index = 0; index < segments.Length - 1; index++)
                {
                    var directoryHandle = OpenOrCreateRelativeDirectoryHandle(
                        directoryHandles[directoryHandles.Count - 1], segments[index]);
                    var information = GetHandleInformation(directoryHandle, segments[index]);
                    if ((information.FileAttributes & FileAttributes.Directory) == 0)
                    {
                        directoryHandle.Dispose();
                        throw new IOException($"Output component '{segments[index]}' is not a directory.");
                    }
                    if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        directoryHandle.Dispose();
                        throw new IOException("outputFile must not traverse a reparse point or junction inside the Unity project.");
                    }

                    var finalDirectory = GetFinalPath(directoryHandle);
                    if (!IsContainedPath(lockedRoot, lockedRootPrefix, finalDirectory, comparison))
                    {
                        directoryHandle.Dispose();
                        throw new IOException("An output directory resolved outside the locked Unity project root.");
                    }
                    directoryHandles.Add(directoryHandle);
                }

                // Tests use this seam to attempt a real directory-to-junction swap at
                // the most hostile point. Every ancestor is already held without delete
                // sharing, so Windows must reject replacement until the write completes.
                afterDirectoriesLocked?.Invoke();

                var fileName = segments[segments.Length - 1];
                using (var fileHandle = OpenOrCreateRelativeOutputFileHandle(
                           directoryHandles[directoryHandles.Count - 1], fileName))
                {
                    var fileInformation = GetHandleInformation(fileHandle, fileName);
                    if ((fileInformation.FileAttributes & FileAttributes.Directory) != 0)
                        throw new IOException("outputFile resolves to a directory.");
                    if ((fileInformation.FileAttributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("outputFile must not be a reparse point or symbolic link.");
                    if (fileInformation.NumberOfLinks > 1)
                        throw new IOException("outputFile must not be a multiply-linked file.");

                    var finalFilePath = GetFinalPath(fileHandle);
                    if (!IsContainedPath(lockedRoot, lockedRootPrefix, finalFilePath, comparison))
                        throw new IOException("The opened output file resolved outside the locked Unity project root.");

                    using (var stream = new FileStream(fileHandle, FileAccess.Write))
                    {
                        stream.SetLength(0);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                    resolvedPath = finalFilePath;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "outputFile could not be securely written: " + ex.GetBaseException().Message;
                return false;
            }
            finally
            {
                for (var index = directoryHandles.Count - 1; index >= 0; index--)
                    directoryHandles[index].Dispose();
            }
        }

        static SafeFileHandle OpenRootDirectoryHandle(string path)
        {
            var handle = CreateFileW(path, FileReadAttributes,
                (uint)(FileShare.Read | FileShare.Write), IntPtr.Zero,
                OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var nativeError = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException($"Could not lock output directory (Win32 {nativeError}).");
            }
            return handle;
        }

        static SafeFileHandle OpenOrCreateRelativeDirectoryHandle(
            SafeFileHandle parent,
            string name)
        {
            var desiredAccess = FileListDirectory | FileReadAttributes | Synchronize;
            var createOptions = NtFileDirectoryFile | NtFileSynchronousIoNonAlert
                | NtFileOpenReparsePoint;
            var status = CreateRelativeHandle(parent, name, desiredAccess,
                (uint)(FileShare.Read | FileShare.Write), NtFileOpen, createOptions,
                out var handle);
            if (status >= 0)
                return handle;
            handle.Dispose();

            status = CreateRelativeHandle(parent, name, desiredAccess,
                (uint)(FileShare.Read | FileShare.Write), NtFileCreate, createOptions,
                out handle);
            if (status >= 0)
                return handle;
            handle.Dispose();

            // A concurrent creator may have won between FILE_OPEN and FILE_CREATE.
            status = CreateRelativeHandle(parent, name, desiredAccess,
                (uint)(FileShare.Read | FileShare.Write), NtFileOpen, createOptions,
                out handle);
            if (status >= 0)
                return handle;
            handle.Dispose();
            throw CreateNtIOException("Could not open or create output directory", status);
        }

        static SafeFileHandle OpenOrCreateRelativeOutputFileHandle(
            SafeFileHandle parent,
            string name)
        {
            var desiredAccess = GenericRead | GenericWrite | Synchronize;
            var createOptions = NtFileNonDirectoryFile | NtFileSynchronousIoNonAlert
                | NtFileOpenReparsePoint;
            var status = CreateRelativeHandle(parent, name, desiredAccess,
                (uint)FileShare.Read, NtFileOpen, createOptions, out var handle);
            if (status >= 0)
                return handle;
            handle.Dispose();

            status = CreateRelativeHandle(parent, name, desiredAccess,
                (uint)FileShare.Read, NtFileCreate, createOptions, out handle);
            if (status >= 0)
                return handle;
            handle.Dispose();

            status = CreateRelativeHandle(parent, name, desiredAccess,
                (uint)FileShare.Read, NtFileOpen, createOptions, out handle);
            if (status >= 0)
                return handle;
            handle.Dispose();
            throw CreateNtIOException("Could not open or create output file", status);
        }

        static int CreateRelativeHandle(
            SafeFileHandle parent,
            string name,
            uint desiredAccess,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            out SafeFileHandle handle)
        {
            using (var objectName = new NativeObjectName(name))
            {
                var objectAttributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf(typeof(ObjectAttributes)),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = objectName.UnicodeStringPointer
                };
                return NtCreateFile(out handle, desiredAccess, ref objectAttributes,
                    out _, IntPtr.Zero, 0, shareAccess, createDisposition,
                    createOptions, IntPtr.Zero, 0);
            }
        }

        static IOException CreateNtIOException(string message, int status)
        {
            var windowsError = RtlNtStatusToDosError(status);
            return new IOException($"{message} (NTSTATUS 0x{unchecked((uint)status):X8}, Win32 {windowsError}).");
        }

        static ByHandleFileInformation GetHandleInformation(SafeFileHandle handle, string path)
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw new IOException($"Could not inspect locked path '{Path.GetFileName(path)}' " +
                    $"(Win32 {Marshal.GetLastWin32Error()}).");
            return information;
        }

        static string GetFinalPath(SafeFileHandle handle)
        {
            var buffer = new StringBuilder(512);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity,
                FileNameNormalized);
            if (length == 0)
                throw new IOException($"Could not resolve a locked path (Win32 {Marshal.GetLastWin32Error()}).");
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity,
                    FileNameNormalized);
                if (length == 0 || length >= buffer.Capacity)
                    throw new IOException($"Could not resolve a locked path (Win32 {Marshal.GetLastWin32Error()}).");
            }
            return NormalizeExtendedWindowsPath(buffer.ToString());
        }

        static string NormalizeExtendedWindowsPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string extendedPrefix = @"\\?\";
            if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(uncPrefix.Length);
            return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(extendedPrefix.Length)
                : path;
        }

        static string EnsureDirectorySuffix(string path)
            => path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;

        static bool IsContainedPath(
            string root,
            string rootPrefix,
            string path,
            StringComparison comparison)
            => string.Equals(path, root, comparison) || path.StartsWith(rootPrefix, comparison);
#endif
    }
}
