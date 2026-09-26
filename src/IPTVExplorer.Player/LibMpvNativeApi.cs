using System.Runtime.InteropServices;
using IPTVExplorer.Core;

namespace IPTVExplorer.Player;

public sealed class LibMpvLibraryLocator
{
    public const string RelativeDirectory = "native/mpv";
    public const string LibraryFileName = "libmpv-2.dll";
    public const string FallbackLibraryFileName = "mpv-2.dll";

    public LibMpvLibraryLocator() : this(AppContext.BaseDirectory) { }

    internal LibMpvLibraryLocator(string baseDirectory)
    {
        var root = Path.GetFullPath(baseDirectory);
        var directory = Path.GetFullPath(Path.Combine(root, "native", "mpv"));
        var relative = Path.GetRelativePath(root, directory);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("Le chemin du moteur vidéo est invalide.");
        PrimaryLibraryPath = Path.Combine(directory, LibraryFileName);
        FallbackLibraryPath = Path.Combine(directory, FallbackLibraryFileName);
        LibraryPath = File.Exists(PrimaryLibraryPath) || !File.Exists(FallbackLibraryPath) ? PrimaryLibraryPath : FallbackLibraryPath;
    }

    public string LibraryPath { get; }
    public string PrimaryLibraryPath { get; }
    public string FallbackLibraryPath { get; }
}

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9
}

internal enum MpvEventKind { None, Shutdown, FileLoaded, EndFile, PropertyChange }
internal sealed record MpvEventValue(MpvEventKind Kind, string? PropertyName = null, object? Value = null);

internal interface ILibMpvApiFactory
{
    ILibMpvApi Create();
}

internal interface ILibMpvApi : IDisposable
{
    nint Create();
    int SetOptionString(nint handle, string name, string value);
    int Initialize(nint handle);
    int Command(nint handle, params string[] arguments);
    int LoadFile(nint handle, string uri, string? httpHeaderFields);
    int SetPropertyString(nint handle, string name, string value);
    int ObserveProperty(nint handle, ulong userData, string name, MpvFormat format);
    MpvEventValue WaitEvent(nint handle, double timeoutSeconds);
    void Wakeup(nint handle);
    void TerminateDestroy(nint handle);
}

internal sealed class LibMpvApiFactory(LibMpvLibraryLocator locator) : ILibMpvApiFactory
{
    public ILibMpvApi Create()
    {
        if (!File.Exists(locator.LibraryPath) || !OperatingSystem.IsWindows()) throw new LibMpvNotInstalledException();
        try { return new LibMpvNativeApi(locator.LibraryPath); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new LibMpvNotInstalledException();
        }
    }
}

internal sealed class LibMpvNativeApi : ILibMpvApi
{
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;
    private readonly nint _module;
    private readonly MpvCreate _create;
    private readonly MpvSetOptionString _setOptionString;
    private readonly MpvInitialize _initialize;
    private readonly MpvCommand _command;
    private readonly MpvCommandNode _commandNode;
    private readonly MpvSetPropertyString _setPropertyString;
    private readonly MpvObserveProperty _observeProperty;
    private readonly MpvWaitEvent _waitEvent;
    private readonly MpvWakeup _wakeup;
    private readonly MpvTerminateDestroy _terminateDestroy;
    private int _disposed;

    public LibMpvNativeApi(string absoluteLibraryPath)
    {
        if (!Path.IsPathFullyQualified(absoluteLibraryPath)) throw new LibMpvNotInstalledException();
        _module = LoadLibraryExW(absoluteLibraryPath, 0, LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
        if (_module == 0) throw new LibMpvNotInstalledException();

        try
        {
            _create = Export<MpvCreate>("mpv_create");
            _setOptionString = Export<MpvSetOptionString>("mpv_set_option_string");
            _initialize = Export<MpvInitialize>("mpv_initialize");
            _command = Export<MpvCommand>("mpv_command");
            _commandNode = Export<MpvCommandNode>("mpv_command_node");
            _setPropertyString = Export<MpvSetPropertyString>("mpv_set_property_string");
            _observeProperty = Export<MpvObserveProperty>("mpv_observe_property");
            _waitEvent = Export<MpvWaitEvent>("mpv_wait_event");
            _wakeup = Export<MpvWakeup>("mpv_wakeup");
            _terminateDestroy = Export<MpvTerminateDestroy>("mpv_terminate_destroy");
        }
        catch
        {
            FreeLibrary(_module);
            throw;
        }
    }

    public nint Create() => _create();
    public int SetOptionString(nint handle, string name, string value) => _setOptionString(handle, name, value);
    public int Initialize(nint handle) => _initialize(handle);

    public int Command(nint handle, params string[] arguments)
    {
        var strings = new nint[arguments.Length];
        nint array = 0;
        try
        {
            for (var index = 0; index < arguments.Length; index++) strings[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
            array = Marshal.AllocHGlobal((arguments.Length + 1) * IntPtr.Size);
            for (var index = 0; index < strings.Length; index++) Marshal.WriteIntPtr(array, index * IntPtr.Size, strings[index]);
            Marshal.WriteIntPtr(array, strings.Length * IntPtr.Size, 0);
            return _command(handle, array);
        }
        finally
        {
            if (array != 0) Marshal.FreeHGlobal(array);
            foreach (var value in strings) if (value != 0) Marshal.FreeCoTaskMem(value);
        }
    }

    public int LoadFile(nint handle, string uri, string? httpHeaderFields)
    {
        const int commandArgumentCount = 5;
        var strings = new List<nint>(6);
        var buffers = new List<nint>(5);
        try
        {
            NativeMpvNode StringNode(string value)
            {
                var pointer = Marshal.StringToCoTaskMemUTF8(value);
                strings.Add(pointer);
                return new NativeMpvNode { String = pointer, Format = MpvFormat.String };
            }

            nint optionValues = 0;
            nint optionKeys = 0;
            var optionCount = 0;
            if (!string.IsNullOrEmpty(httpHeaderFields))
            {
                optionCount = 1;
                optionValues = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMpvNode>());
                buffers.Add(optionValues);
                Marshal.StructureToPtr(StringNode(httpHeaderFields), optionValues, false);
                optionKeys = Marshal.AllocHGlobal(IntPtr.Size);
                buffers.Add(optionKeys);
                Marshal.WriteIntPtr(optionKeys, StringNode("http-header-fields").String);
            }

            var optionListPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMpvNodeList>());
            buffers.Add(optionListPointer);
            Marshal.StructureToPtr(new NativeMpvNodeList { Count = optionCount, Values = optionValues, Keys = optionKeys }, optionListPointer, false);
            var optionMap = new NativeMpvNode { List = optionListPointer, Format = MpvFormat.NodeMap };

            var commandValues = Marshal.AllocHGlobal(commandArgumentCount * Marshal.SizeOf<NativeMpvNode>());
            buffers.Add(commandValues);
            var arguments = new[] { StringNode("loadfile"), StringNode(uri), StringNode("replace"), StringNode("-1"), optionMap };
            for (var index = 0; index < arguments.Length; index++)
                Marshal.StructureToPtr(arguments[index], commandValues + index * Marshal.SizeOf<NativeMpvNode>(), false);

            var commandListPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMpvNodeList>());
            buffers.Add(commandListPointer);
            Marshal.StructureToPtr(new NativeMpvNodeList { Count = commandArgumentCount, Values = commandValues, Keys = 0 }, commandListPointer, false);
            var command = new NativeMpvNode { List = commandListPointer, Format = MpvFormat.NodeArray };
            return _commandNode(handle, ref command, 0);
        }
        finally
        {
            for (var index = buffers.Count - 1; index >= 0; index--) Marshal.FreeHGlobal(buffers[index]);
            for (var index = strings.Count - 1; index >= 0; index--) Marshal.FreeCoTaskMem(strings[index]);
        }
    }

    public int SetPropertyString(nint handle, string name, string value) => _setPropertyString(handle, name, value);
    public int ObserveProperty(nint handle, ulong userData, string name, MpvFormat format) => _observeProperty(handle, userData, name, format);

    public MpvEventValue WaitEvent(nint handle, double timeoutSeconds)
    {
        var pointer = _waitEvent(handle, timeoutSeconds);
        if (pointer == 0) return new(MpvEventKind.None);
        var value = Marshal.PtrToStructure<NativeMpvEvent>(pointer);
        return value.EventId switch
        {
            NativeMpvEventId.None => new(MpvEventKind.None),
            NativeMpvEventId.Shutdown => new(MpvEventKind.Shutdown),
            NativeMpvEventId.FileLoaded => new(MpvEventKind.FileLoaded),
            NativeMpvEventId.EndFile => new(MpvEventKind.EndFile),
            NativeMpvEventId.PropertyChange => ReadProperty(value.Data),
            _ => new(MpvEventKind.None)
        };
    }

    public void Wakeup(nint handle) => _wakeup(handle);
    public void TerminateDestroy(nint handle) => _terminateDestroy(handle);

    private static MpvEventValue ReadProperty(nint pointer)
    {
        if (pointer == 0) return new(MpvEventKind.PropertyChange);
        var property = Marshal.PtrToStructure<NativeMpvEventProperty>(pointer);
        var name = Marshal.PtrToStringUTF8(property.Name);
        object? value = property.Format switch
        {
            MpvFormat.Flag when property.Data != 0 => Marshal.ReadInt32(property.Data) != 0,
            MpvFormat.Int64 when property.Data != 0 => Marshal.ReadInt64(property.Data),
            MpvFormat.Double when property.Data != 0 => Marshal.PtrToStructure<double>(property.Data),
            MpvFormat.String or MpvFormat.OsdString when property.Data != 0 => ReadStringPointer(property.Data),
            MpvFormat.Node when property.Data != 0 && name == "track-list" => ReadTracks(Marshal.PtrToStructure<NativeMpvNode>(property.Data)),
            _ => null
        };
        return new(MpvEventKind.PropertyChange, name, value);
    }

    private static string? ReadStringPointer(nint pointer)
    {
        var value = Marshal.ReadIntPtr(pointer);
        return value == 0 ? null : Marshal.PtrToStringUTF8(value);
    }

    private static IReadOnlyList<MediaTrack> ReadTracks(NativeMpvNode root)
    {
        if (root.Format != MpvFormat.NodeArray || root.List == 0) return [];
        var list = Marshal.PtrToStructure<NativeMpvNodeList>(root.List);
        if (list.Count <= 0 || list.Values == 0) return [];
        var tracks = new List<MediaTrack>(list.Count);
        for (var index = 0; index < list.Count; index++)
        {
            var node = ReadNode(list.Values, index);
            if (node.Format != MpvFormat.NodeMap) continue;
            var id = NodeInt64(node, "id");
            var type = NodeString(node, "type") switch
            {
                "audio" => MediaTrackType.Audio,
                "video" => MediaTrackType.Video,
                "sub" => MediaTrackType.Subtitle,
                _ => (MediaTrackType?)null
            };
            if (id is null || type is null) continue;
            tracks.Add(new MediaTrack(
                id.Value,
                type.Value,
                NodeString(node, "lang"),
                NodeString(node, "title"),
                NodeString(node, "codec"),
                NodeBool(node, "selected") ?? false,
                NodeBool(node, "default") ?? false,
                NodeBool(node, "forced") ?? false,
                ToInt32(NodeInt64(node, "demux-channel-count")),
                ToInt32(NodeInt64(node, "demux-samplerate")),
                ToInt32(NodeInt64(node, "demux-w")),
                ToInt32(NodeInt64(node, "demux-h")),
                NodeDouble(node, "demux-fps"),
                ReadHdr(node)));
        }
        return tracks;
    }

    private static bool? ReadHdr(NativeMpvNode node)
    {
        var direct = NodeBool(node, "hdr");
        if (direct is not null) return direct;
        var transfer = NodeString(node, "color-transfer");
        return transfer is null ? null : transfer.Contains("pq", StringComparison.OrdinalIgnoreCase) || transfer.Contains("hlg", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ToInt32(long? value) => value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;

    private static string? NodeString(NativeMpvNode map, string name)
    {
        var node = MapValue(map, name);
        return node is { Format: MpvFormat.String or MpvFormat.OsdString, String: not 0 } ? Marshal.PtrToStringUTF8(node.Value.String) : null;
    }

    private static long? NodeInt64(NativeMpvNode map, string name)
    {
        var node = MapValue(map, name);
        return node is { Format: MpvFormat.Int64 } ? node.Value.Int64 : null;
    }

    private static double? NodeDouble(NativeMpvNode map, string name)
    {
        var node = MapValue(map, name);
        return node switch
        {
            { Format: MpvFormat.Double } => node.Value.Double,
            { Format: MpvFormat.Int64 } => node.Value.Int64,
            _ => null
        };
    }

    private static bool? NodeBool(NativeMpvNode map, string name)
    {
        var node = MapValue(map, name);
        return node is { Format: MpvFormat.Flag } ? node.Value.Flag != 0 : null;
    }

    private static NativeMpvNode? MapValue(NativeMpvNode map, string name)
    {
        if (map.Format != MpvFormat.NodeMap || map.List == 0) return null;
        var list = Marshal.PtrToStructure<NativeMpvNodeList>(map.List);
        if (list.Count <= 0 || list.Values == 0 || list.Keys == 0) return null;
        for (var index = 0; index < list.Count; index++)
        {
            var keyPointer = Marshal.ReadIntPtr(list.Keys, index * IntPtr.Size);
            if (keyPointer != 0 && string.Equals(Marshal.PtrToStringUTF8(keyPointer), name, StringComparison.Ordinal)) return ReadNode(list.Values, index);
        }
        return null;
    }

    private static NativeMpvNode ReadNode(nint values, int index) => Marshal.PtrToStructure<NativeMpvNode>(values + index * Marshal.SizeOf<NativeMpvNode>());

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) FreeLibrary(_module);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string fileName, nint file, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint MpvCreate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MpvSetOptionString(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MpvInitialize(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MpvCommand(nint handle, nint arguments);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MpvCommandNode(nint handle, ref NativeMpvNode arguments, nint result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MpvSetPropertyString(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MpvObserveProperty(nint handle, ulong userData, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, MpvFormat format);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint MpvWaitEvent(nint handle, double timeoutSeconds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MpvWakeup(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MpvTerminateDestroy(nint handle);

    private enum NativeMpvEventId
    {
        None = 0,
        Shutdown = 1,
        EndFile = 7,
        FileLoaded = 8,
        PropertyChange = 22
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMpvEvent
    {
        public readonly NativeMpvEventId EventId;
        public readonly int Error;
        public readonly ulong ReplyUserdata;
        public readonly nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMpvEventProperty
    {
        public readonly nint Name;
        public readonly MpvFormat Format;
        public readonly nint Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct NativeMpvNode
    {
        [FieldOffset(0)] public nint String;
        [FieldOffset(0)] public long Int64;
        [FieldOffset(0)] public double Double;
        [FieldOffset(0)] public int Flag;
        [FieldOffset(0)] public nint List;
        [FieldOffset(8)] public MpvFormat Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMpvNodeList
    {
        public int Count;
        public nint Values;
        public nint Keys;
    }
}
