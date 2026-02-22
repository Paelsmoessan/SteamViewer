using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SteamViewer.Common.Protocol;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace SteamViewer.Platform.Windows.Clipboard;

/// <summary>
/// COM IDataObject that presents remote files as pasteable clipboard items.
/// Advertises CFSTR_FILEDESCRIPTORW and CFSTR_FILECONTENTS formats.
/// When Explorer pastes, it requests CFSTR_FILECONTENTS which returns an IStream
/// that fetches data on-demand from the remote machine.
///
/// Source attribution: Pattern derived from VirtualFileDataObject (MIT license)
/// and RustDesk/FreeRDP clipboard redirection (Apache-2.0).
/// </summary>
public sealed class VirtualFileDataObject : IComDataObject
{
    private readonly ClipboardFileInfo[] _files;
    private readonly Func<ClipboardFileMessage.FileContentsRequest, Task> _sendRequest;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> _pendingRequests;

    // Registered clipboard format IDs (registered once, cached)
    private static readonly ushort CF_FILEDESCRIPTORW =
        RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly ushort CF_FILECONTENTS =
        RegisterClipboardFormat("FileContents");
    private static readonly ushort CF_PREFERREDDROPEFFECT =
        RegisterClipboardFormat("Preferred DropEffect");

    private const int DROPEFFECT_COPY = 1;

    // FILEDESCRIPTORW struct layout
    private const int FD_FILESIZE = 0x00000040;
    private const int FD_WRITESTIME = 0x00000020;
    private const int FD_ATTRIBUTES = 0x00000004;
    private const int FD_PROGRESSUI = 0x00004000;
    private const int FILEDESCRIPTORW_SIZE = 592; // On x64: flags(4) + clsid(16) + size(8) + point(8) + attrs(4) + times(24) + filesizeh(4) + filesizel(4) + name(520)

    public VirtualFileDataObject(
        ClipboardFileInfo[] files,
        Func<ClipboardFileMessage.FileContentsRequest, Task> sendRequest,
        ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> pendingRequests)
    {
        _files = files;
        _sendRequest = sendRequest;
        _pendingRequests = pendingRequests;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = new STGMEDIUM();

        if (format.cfFormat == CF_FILEDESCRIPTORW &&
            (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = BuildFileGroupDescriptor();
            return;
        }

        if (format.cfFormat == CF_FILECONTENTS &&
            (format.tymed & TYMED.TYMED_ISTREAM) != 0 &&
            format.lindex >= 0 && format.lindex < _files.Length)
        {
            var stream = new RemoteFileStream(
                format.lindex,
                _files[format.lindex].FileName,
                _files[format.lindex].FileSize,
                _sendRequest,
                _pendingRequests);

            medium.tymed = TYMED.TYMED_ISTREAM;
            medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
            return;
        }

        if (format.cfFormat == CF_PREFERREDDROPEFFECT &&
            (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = BuildPreferredDropEffect();
            return;
        }

        Marshal.ThrowExceptionForHR(DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
    {
        Marshal.ThrowExceptionForHR(unchecked((int)0x80004001)); // E_NOTIMPL
    }

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == CF_FILEDESCRIPTORW && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
            return S_OK;
        if (format.cfFormat == CF_FILECONTENTS && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
            return S_OK;
        if (format.cfFormat == CF_PREFERREDDROPEFFECT && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
            return S_OK;

        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        return DATA_S_SAMEFORMATETC;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        Marshal.ThrowExceptionForHR(unchecked((int)0x80004001)); // E_NOTIMPL
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
            Marshal.ThrowExceptionForHR(unchecked((int)0x80004001));

        var formats = new FORMATETC[]
        {
            new()
            {
                cfFormat = (short)CF_FILEDESCRIPTORW,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                ptd = IntPtr.Zero,
                tymed = TYMED.TYMED_HGLOBAL
            },
            new()
            {
                cfFormat = (short)CF_FILECONTENTS,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                ptd = IntPtr.Zero,
                tymed = TYMED.TYMED_ISTREAM
            },
            new()
            {
                cfFormat = (short)CF_PREFERREDDROPEFFECT,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                ptd = IntPtr.Zero,
                tymed = TYMED.TYMED_HGLOBAL
            }
        };

        return new FormatEtcEnumerator(formats);
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return unchecked((int)0x80040003); // OLE_E_ADVISENOTSUPPORTED
    }

    public void DUnadvise(int connection)
    {
        Marshal.ThrowExceptionForHR(unchecked((int)0x80040003));
    }

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return unchecked((int)0x80040003);
    }

    #region FILEGROUPDESCRIPTORW Builder

    /// <summary>
    /// Build a FILEGROUPDESCRIPTORW in HGLOBAL memory.
    /// Layout: [cItems (4 bytes)] [FILEDESCRIPTORW × N]
    /// </summary>
    private IntPtr BuildFileGroupDescriptor()
    {
        int headerSize = 4; // cItems (UINT)
        int totalSize = headerSize + _files.Length * FILEDESCRIPTORW_SIZE;

        IntPtr hGlobal = GlobalAlloc(GHND, (UIntPtr)totalSize);
        if (hGlobal == IntPtr.Zero)
            Marshal.ThrowExceptionForHR(unchecked((int)0x8007000E)); // E_OUTOFMEMORY

        IntPtr pGlobal = GlobalLock(hGlobal);
        try
        {
            // Write cItems
            Marshal.WriteInt32(pGlobal, _files.Length);

            for (int i = 0; i < _files.Length; i++)
            {
                IntPtr pDescriptor = pGlobal + headerSize + i * FILEDESCRIPTORW_SIZE;
                WriteFileDescriptor(pDescriptor, _files[i]);
            }
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        return hGlobal;
    }

    private static void WriteFileDescriptor(IntPtr ptr, ClipboardFileInfo file)
    {
        // Zero the entire struct first
        for (int i = 0; i < FILEDESCRIPTORW_SIZE; i++)
            Marshal.WriteByte(ptr, i, 0);

        // dwFlags at offset 0
        int flags = FD_FILESIZE | FD_PROGRESSUI;
        if (file.FileAttributes != 0) flags |= FD_ATTRIBUTES;
        if (file.LastWriteTime != 0) flags |= FD_WRITESTIME;
        Marshal.WriteInt32(ptr, 0, flags);

        // dwFileAttributes at offset 36
        if (file.FileAttributes != 0)
            Marshal.WriteInt32(ptr, 36, (int)file.FileAttributes);

        // ftLastWriteTime at offset 56 (FILETIME = 8 bytes)
        if (file.LastWriteTime != 0)
            Marshal.WriteInt64(ptr, 56, file.LastWriteTime);

        // nFileSizeHigh at offset 64, nFileSizeLow at offset 68
        Marshal.WriteInt32(ptr, 64, (int)(file.FileSize >> 32));
        Marshal.WriteInt32(ptr, 68, (int)(file.FileSize & 0xFFFFFFFF));

        // cFileName at offset 72 (WCHAR[260] = 520 bytes)
        int nameOffset = 72;
        int maxChars = Math.Min(file.FileName.Length, 259); // MAX_PATH - 1
        for (int c = 0; c < maxChars; c++)
        {
            Marshal.WriteInt16(ptr, nameOffset + c * 2, file.FileName[c]);
        }
        // Null terminator already zero from initial zeroing
    }

    private static IntPtr BuildPreferredDropEffect()
    {
        IntPtr hGlobal = GlobalAlloc(GHND, (UIntPtr)4);
        IntPtr pGlobal = GlobalLock(hGlobal);
        Marshal.WriteInt32(pGlobal, DROPEFFECT_COPY);
        GlobalUnlock(hGlobal);
        return hGlobal;
    }

    #endregion

    #region IEnumFORMATETC Implementation

    private sealed class FormatEtcEnumerator : IEnumFORMATETC
    {
        private readonly FORMATETC[] _formats;
        private int _current;

        public FormatEtcEnumerator(FORMATETC[] formats, int current = 0)
        {
            _formats = formats;
            _current = current;
        }

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            int fetched = 0;
            while (fetched < celt && _current < _formats.Length)
            {
                rgelt[fetched] = _formats[_current];
                _current++;
                fetched++;
            }

            if (pceltFetched != null && pceltFetched.Length > 0)
                pceltFetched[0] = fetched;

            return fetched == celt ? S_OK : S_FALSE;
        }

        public int Skip(int celt)
        {
            _current += celt;
            return _current <= _formats.Length ? S_OK : S_FALSE;
        }

        public int Reset()
        {
            _current = 0;
            return S_OK;
        }

        public void Clone(out IEnumFORMATETC newEnum)
        {
            newEnum = new FormatEtcEnumerator(_formats, _current);
        }
    }

    #endregion

    #region Constants

    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DATA_S_SAMEFORMATETC = 0x00040130;
    private const uint GHND = 0x0042; // GMEM_MOVEABLE | GMEM_ZEROINIT

    #endregion

    #region P/Invoke

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    #endregion
}
