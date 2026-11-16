using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CKBusbarBridge.views
{
    /// <summary>
    /// 使用 Windows 通用文件对话框的文件夹模式，与清单保存窗口保持一致的浏览体验。
    /// </summary>
    internal sealed class ProjectRootFolderDialog
    {
        private const int CancelledHResult = unchecked((int)0x800704C7);
        private const uint FileSystemPathDisplayName = 0x80058000;

        internal bool TrySelect(string initialDirectory, out string selectedDirectory)
        {
            selectedDirectory = null;
            var dialog = (IFileDialog)new FileOpenDialog();
            IShellItem initialFolder = null;
            IShellItem selectedFolder = null;
            try
            {
                dialog.SetOptions(FolderDialogOptions.PickFolders |
                    FolderDialogOptions.ForceFileSystem |
                    FolderDialogOptions.PathMustExist |
                    FolderDialogOptions.NoChangeDirectory);
                dialog.SetTitle("设置项目根目录");
                dialog.SetOkButtonLabel("选择文件夹");

                if (Directory.Exists(initialDirectory))
                {
                    var shellItemId = typeof(IShellItem).GUID;
                    SHCreateItemFromParsingName(Path.GetFullPath(initialDirectory),
                        IntPtr.Zero, ref shellItemId, out initialFolder);
                    // 显式定位到已保存的根目录，避免被其他文件对话框的浏览历史覆盖。
                    dialog.SetFolder(initialFolder);
                }

                var result = dialog.Show(GetActiveWindow());
                if (result == CancelledHResult)
                {
                    return false;
                }

                Marshal.ThrowExceptionForHR(result);
                dialog.GetResult(out selectedFolder);
                selectedFolder.GetDisplayName(FileSystemPathDisplayName, out var pathPointer);
                try
                {
                    selectedDirectory = Marshal.PtrToStringUni(pathPointer);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPointer);
                }

                return Directory.Exists(selectedDirectory);
            }
            finally
            {
                if (selectedFolder != null) Marshal.ReleaseComObject(selectedFolder);
                if (initialFolder != null) Marshal.ReleaseComObject(initialFolder);
                Marshal.ReleaseComObject(dialog);
            }
        }

        [Flags]
        private enum FolderDialogOptions : uint
        {
            NoChangeDirectory = 0x8,
            PickFolders = 0x20,
            ForceFileSystem = 0x40,
            PathMustExist = 0x800
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetActiveWindow();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr bindingContext, ref Guid interfaceId, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        // COM 方法须按原生接口的顺序声明，包括未使用的方法，以保持虚表槽位一致。
        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(FolderDialogOptions options);
            void GetOptions(out FolderDialogOptions options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, uint placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid clientGuid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindingContext, ref Guid handlerId,
                ref Guid interfaceId, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint displayNameType, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
