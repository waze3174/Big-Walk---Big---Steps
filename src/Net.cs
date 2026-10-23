using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace BigSteps;

// side channel between players who run this mod, beside big walk's own networking (mirror over steam/ip).
// every other player in the lobby gets a few hellos; the ones that answer are modded, the rest are left alone.
// steam p2p first (big walk ships steam_api64 and steam is running), epic p2p as a fallback.
// the flat steam p2p calls follow the CC0 Baby Steps Multiplayer mod (caleborchard/Baby-Steps-Multiplayer-Mod-Client)
static class Net
{
    internal const byte Magic = 0xBB, Proto = 2;     // 2: leg packets carry each foot's height above the ground
    internal enum Kind : byte { Hello = 1, HelloAck = 2, Bye = 3, Legs = 4, Settings = 5 }
    const int HelloTries = 10;
    const float HelloEvery = 1f, ScanEvery = 1f;

    internal class Peer
    {
        public string key, name = "?", epic = "", version = "";
        public ulong steam;
        public IntPtr puid;            // epic handle, resolved from the epic id string
        public PlayerCharacter pc;
        public bool modded, toldVanilla, seen;
        public int hellos;
        public float nextHello, lastHeard;
        public Transport via;          // the transport their replies came in on
        public override string ToString() => $"{name} (steam {steam}, epic {(epic.Length > 0 ? epic : "-")})";
    }

    internal static readonly Dictionary<string, Peer> Peers = new();
    internal static event Action<Peer, Kind, byte[], int> Received;   // modded peer, kind, packet, payload start
    static float nextScan;
    static bool started;
    static readonly byte[] buf = new byte[4096];

    internal static int ModdedCount { get { int n = 0; foreach (var p in Peers.Values) if (p.modded) n++; return n; } }
    internal static string Status => !started ? "mp: off" :
        $"mp: {ModdedCount} modded / {Peers.Count} others  steam {(Steam.Ok ? "ok" : "no")}  epic {(Epic.Ok ? "ok" : "no")}";

    internal static void Update()
    {
        if (!started) { started = true; Steam.Init(); Epic.Init(); }
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + ScanEvery;
            if (!Steam.Ok && Time.unscaledTime < 120) Steam.Init();     // steam may come up after us
            Scan(); Epic.Refresh();
        }
        if (Steam.Ok) Steam.Poll();
        if (Epic.Ok) Epic.Poll();
        float now = Time.unscaledTime;
        foreach (var p in Peers.Values)
        {
            if (p.modded || p.hellos >= HelloTries || now < p.nextHello) continue;
            p.hellos++; p.nextHello = now + HelloEvery;
            var h = Hello(Kind.Hello);
            if (Steam.Ok && p.steam != 0) Steam.Send(p.steam, h, true);
            if (Epic.Ok && p.puid != IntPtr.Zero) Epic.Send(p.puid, h, true);
            if (p.hellos == HelloTries && !p.toldVanilla) { p.toldVanilla = true; Plugin.L.LogInfo($"net: {p} looks vanilla (no reply to {HelloTries} hellos)"); }
        }
    }

    // big walk's player list -> peers (join, leave)
    static void Scan()
    {
        var all = PlayerCharacter.allPlayerCharacters;
        foreach (var p in Peers.Values) p.seen = false;
        if (all != null)
            for (int i = 0; i < all.Count; i++)
            {
                var pc = all[i];
                if (pc == null || pc.isLocalPlayer) continue;
                PlayerNetworking pn;
                try { pn = pc.playerNetworking; } catch { continue; }
                if (pn == null) continue;
                ulong steam = pn.userPlatformId;
                string epic = pn.epicUserId ?? "";
                if (steam == 0 && epic.Length == 0) continue;     // ids not synced yet
                // one peer per bird: the ids fill in a few frames apart, they're updated in place
                string key = pc.Pointer.ToString();
                if (!Peers.TryGetValue(key, out var peer))
                {
                    peer = new Peer { key = key, steam = steam, epic = epic, name = Name(pn) };
                    Peers[key] = peer;
                    Plugin.L.LogInfo($"net: player joined: {peer}");
                    Legs.Inst.Handover();
                    if (Steam.Ok && steam != 0) Steam.Accept(steam);
                }
                if (peer.steam == 0 && steam != 0) { peer.steam = steam; if (Steam.Ok) Steam.Accept(steam); }
                if (peer.epic.Length == 0 && epic.Length > 0) peer.epic = epic;
                if (peer.name == "?") peer.name = Name(pn);
                peer.pc = pc;
                peer.seen = true;
            }
        List<string> gone = null;
        foreach (var p in Peers.Values) if (!p.seen) (gone ??= new()).Add(p.key);
        if (gone != null)
            foreach (var k in gone)
            {
                Plugin.L.LogInfo($"net: player left: {Peers[k]}");
                Peers.Remove(k);
            }
    }

    static string Name(PlayerNetworking pn)
    {
        foreach (var f in new Func<string>[] { () => pn.moderationNameSanitized, () => pn.moderationName, () => pn.Networkusername })
            try { var s = f(); if (!string.IsNullOrWhiteSpace(s)) return s; } catch { }
        return "?";
    }

    internal static byte[] Hello(Kind k)
    {
        var w = new Writer(k);
        w.Str(Plugin.Version);
        w.U64(Steam.Me);
        w.Str(Epic.MeString ?? "");
        return w.Done();
    }

    // modded peers only
    internal static void Send(Peer p, byte[] data, bool reliable)
    {
        if (p.via == Transport.Steam && Steam.Ok) Steam.Send(p.steam, data, reliable);
        else if (p.via == Transport.Epic && Epic.Ok) Epic.Send(p.puid, data, reliable);
    }

    internal static void SendAll(byte[] data, bool reliable)
    {
        foreach (var p in Peers.Values) if (p.modded) Send(p, data, reliable);
    }

    internal static void Shutdown()
    {
        if (!started) return;
        var bye = new Writer(Kind.Bye).Done();
        SendAll(bye, true);
    }

    internal enum Transport { None, Steam, Epic }

    static void OnPacket(Transport t, ulong steamFrom, string epicFrom, IntPtr puidFrom, byte[] d, int n)
    {
        if (n < 3 || d[0] != Magic) return;
        if (d[1] != Proto)
        {
            Plugin.L.LogWarning($"net: packet with protocol {d[1]} (ours {Proto}) from {(t == Transport.Steam ? steamFrom.ToString() : epicFrom)}: update the mod on both sides");
            return;
        }
        var k = (Kind)d[2];
        Peer p = null;
        foreach (var q in Peers.Values)
            if ((t == Transport.Steam && q.steam == steamFrom && steamFrom != 0) || (t == Transport.Epic && q.epic.Length > 0 && q.epic == epicFrom)) { p = q; break; }
        if (p == null) return;     // not (yet) in big walk's player list
        if (t == Transport.Epic && p.puid == IntPtr.Zero) p.puid = puidFrom;
        p.lastHeard = Time.unscaledTime;
        if (k == Kind.Hello || k == Kind.HelloAck)
        {
            var r = new Reader(d, n, 3);
            string ver = r.Str();
            bool fresh = !p.modded;
            p.modded = true;
            p.version = ver;
            if (p.via == Transport.None || t == Transport.Steam) p.via = t;
            if (fresh) Settings.PeerJoined();
            if (fresh) Plugin.L.LogInfo($"net: {p} runs Big Steps {ver} (via {t})" + (ver != Plugin.Version ? $" - we run {Plugin.Version}, things may not match" : ""));
            if (k == Kind.Hello)
            {
                var a = Hello(Kind.HelloAck);
                if (t == Transport.Steam) Steam.Send(steamFrom, a, true); else Epic.Send(puidFrom, a, true);
            }
            return;
        }
        if (!p.modded) return;
        if (k == Kind.Bye) { p.modded = false; p.hellos = HelloTries; p.toldVanilla = true; Plugin.L.LogInfo($"net: {p} turned the mod off / quit"); }
        try { Received?.Invoke(p, k, d, 3); }
        catch (Exception e) { Plugin.L.LogWarning($"net: handling {k} from {p.name}: {e.Message}"); }
    }

    // ---------------- packets ----------------

    internal class Writer
    {
        readonly List<byte> b = new(64);
        public Writer(Kind k) { b.Add(Magic); b.Add(Proto); b.Add((byte)k); }
        public void U8(byte v) => b.Add(v);
        public void U16(ushort v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        public void U64(ulong v) { for (int i = 0; i < 8; i++) b.Add((byte)(v >> (8 * i))); }
        public void F(float v) { b.AddRange(BitConverter.GetBytes(v)); }
        public void V(Vector3 v) { F(v.x); F(v.y); F(v.z); }
        public void Str(string s) { var x = Encoding.UTF8.GetBytes(s ?? ""); U8((byte)Math.Min(x.Length, 255)); for (int i = 0; i < Math.Min(x.Length, 255); i++) b.Add(x[i]); }
        public byte[] Done() => b.ToArray();
    }

    internal class Reader
    {
        readonly byte[] d; readonly int n; int i;
        public Reader(byte[] d, int n, int start) { this.d = d; this.n = n; i = start; }
        public bool Ok(int need) => i + need <= n;
        public byte U8() => Ok(1) ? d[i++] : (byte)0;
        public ushort U16() { if (!Ok(2)) return 0; ushort v = (ushort)(d[i] | d[i + 1] << 8); i += 2; return v; }
        public ulong U64() { if (!Ok(8)) return 0; ulong v = 0; for (int k = 0; k < 8; k++) v |= (ulong)d[i + k] << (8 * k); i += 8; return v; }
        public float F() { if (!Ok(4)) return 0; float v = BitConverter.ToSingle(d, i); i += 4; return v; }
        public Vector3 V() => new(F(), F(), F());
        public string Str() { int len = U8(); if (!Ok(len)) return ""; var s = Encoding.UTF8.GetString(d, i, len); i += len; return s; }
    }

    // ---------------- steam (legacy ISteamNetworking p2p, own channel; big walk's mirror transport uses 0..2) ----------------

    static class Steam
    {
        const string Dll = "steam_api64";
        const int Channel = 47;
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SteamAPI_SteamNetworking_v006();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SteamAPI_SteamUser_v021();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SteamAPI_ISteamNetworking_SendP2PPacket(IntPtr self, ulong to, byte[] data, uint len, int sendType, int channel);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SteamAPI_ISteamNetworking_IsP2PPacketAvailable(IntPtr self, out uint size, int channel);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SteamAPI_ISteamNetworking_ReadP2PPacket(IntPtr self, byte[] dest, uint cap, out uint size, out ulong from, int channel);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SteamAPI_ISteamNetworking_AcceptP2PSessionWithUser(IntPtr self, ulong who);

        static IntPtr net;
        internal static bool Ok;
        internal static ulong Me;
        static int sendFails;
        static bool told;

        internal static void Init()
        {
            try
            {
                net = SteamAPI_SteamNetworking_v006();
                var user = SteamAPI_SteamUser_v021();
                if (user != IntPtr.Zero) Me = SteamAPI_ISteamUser_GetSteamID(user);
                Ok = net != IntPtr.Zero && Me != 0;
                if (Ok) Plugin.L.LogInfo($"net: steam p2p ready (we are {Me}, channel {Channel})");
                else if (!told) { told = true; Plugin.L.LogInfo("net: steam not up (yet), steam p2p off"); }
                if (Ok) foreach (var p in Peers.Values) if (p.steam != 0) Accept(p.steam);
            }
            catch (Exception e) { Ok = false; if (!told) { told = true; Plugin.L.LogInfo("net: steam p2p unavailable: " + e.Message); } }
        }

        internal static void Accept(ulong who) { try { SteamAPI_ISteamNetworking_AcceptP2PSessionWithUser(net, who); } catch { } }

        // 0 unreliable, 2 reliable
        internal static void Send(ulong to, byte[] d, bool reliable)
        {
            if (!SteamAPI_ISteamNetworking_SendP2PPacket(net, to, d, (uint)d.Length, reliable ? 2 : 0, Channel) && sendFails++ < 5)
                Plugin.L.LogWarning($"net: steam send to {to} failed");
        }

        internal static void Poll()
        {
            for (int guard = 0; guard < 256 && SteamAPI_ISteamNetworking_IsP2PPacketAvailable(net, out uint size, Channel); guard++)
            {
                var dest = size <= buf.Length ? buf : new byte[size];
                if (!SteamAPI_ISteamNetworking_ReadP2PPacket(net, dest, (uint)dest.Length, out uint got, out ulong from, Channel)) break;
                OnPacket(Transport.Steam, from, null, IntPtr.Zero, dest, (int)got);
            }
        }
    }

    // ---------------- epic (eos p2p via the sdk big walk already runs; older api versions so any newer sdk takes them) ----------------

    static unsafe class Epic
    {
        const string Dll = "EOSSDK-Win64-Shipping";
        const byte Channel = 47;
        const int ResultSuccess = 0;

        [StructLayout(LayoutKind.Sequential)] struct SocketId { public int ApiVersion; public fixed byte Name[33]; }
        [StructLayout(LayoutKind.Sequential)] struct SendOpts { public int ApiVersion; public IntPtr Local, Remote; public SocketId* Socket; public byte Channel; public uint Len; public byte* Data; public int AllowDelayed; public int Reliability; }
        [StructLayout(LayoutKind.Sequential)] struct SizeOpts { public int ApiVersion; public IntPtr Local; public byte* Channel; }
        [StructLayout(LayoutKind.Sequential)] struct RecvOpts { public int ApiVersion; public IntPtr Local; public uint Max; public byte* Channel; }
        [StructLayout(LayoutKind.Sequential)] struct AcceptOpts { public int ApiVersion; public IntPtr Local, Remote; public SocketId* Socket; }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr EOS_Platform_GetP2PInterface(IntPtr platform);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int EOS_P2P_SendPacket(IntPtr p2p, SendOpts* o);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int EOS_P2P_GetNextReceivedPacketSize(IntPtr p2p, SizeOpts* o, uint* size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int EOS_P2P_ReceivePacket(IntPtr p2p, RecvOpts* o, IntPtr* peer, SocketId* socket, byte* channel, byte* data, uint* written);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int EOS_P2P_AcceptConnection(IntPtr p2p, AcceptOpts* o);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr EOS_ProductUserId_FromString(byte* s);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int EOS_ProductUserId_ToString(IntPtr id, byte* buf, int* len);

        static IntPtr p2p, me;
        internal static bool Ok;
        internal static string MeString;
        static bool failed, told, comparedIds;
        static int sendFails;
        static SocketId socket;

        internal static void Init()
        {
            socket.ApiVersion = 1;
            var name = Encoding.ASCII.GetBytes("BigSteps");
            fixed (SocketId* s = &socket) for (int i = 0; i < name.Length; i++) s->Name[i] = name[i];
            Refresh();
        }

        // epic logs in a while after launch (and again after reconnects), so the handles are picked up late
        internal static void Refresh()
        {
            if (failed) return;
            try
            {
                var mgr = PlayEveryWare.EpicOnlineServices.EOSManager.Instance;
                var plat = mgr?.GetEOSPlatformInterface();
                var id = mgr?.GetProductUserId();
                IntPtr platH = plat == null ? IntPtr.Zero : plat.InnerHandle, idH = id == null ? IntPtr.Zero : id.InnerHandle;
                if (platH == IntPtr.Zero || idH == IntPtr.Zero) { Ok = false; return; }
                if (p2p == IntPtr.Zero) p2p = EOS_Platform_GetP2PInterface(platH);
                if (idH != me) { me = idH; MeString = IdString(me); }
                bool was = Ok;
                Ok = p2p != IntPtr.Zero && me != IntPtr.Zero;
                if (Ok && !was) Plugin.L.LogInfo($"net: epic p2p ready (we are {MeString})");
                if (Ok && !comparedIds && Game.Pc != null)
                {
                    string ours = "";
                    try { ours = Game.Pc.playerNetworking.epicUserId ?? ""; } catch { }
                    comparedIds = ours.Length > 0;
                    if (ours.Length > 0 && ours != MeString) Plugin.L.LogWarning($"net: big walk's epic id for us is '{ours}', not our epic product id - epic fallback may not find peers");
                }
                foreach (var p in Peers.Values)
                    if (p.puid == IntPtr.Zero && p.epic.Length > 0 && (p.puid = FromString(p.epic)) != IntPtr.Zero) Accept(p.puid);
            }
            catch (Exception e)
            {
                failed = true; Ok = false;
                Plugin.L.LogInfo("net: epic p2p unavailable: " + e.Message);
            }
        }

        static IntPtr FromString(string s)
        {
            var b = Encoding.ASCII.GetBytes(s + "\0");
            fixed (byte* p = b) return EOS_ProductUserId_FromString(p);
        }

        static string IdString(IntPtr id)
        {
            byte* b = stackalloc byte[64];
            int len = 64;
            if (EOS_ProductUserId_ToString(id, b, &len) != ResultSuccess) return null;
            int n = 0; while (n < len && b[n] != 0) n++;
            return Encoding.ASCII.GetString(b, n);
        }

        static void Accept(IntPtr who)
        {
            fixed (SocketId* s = &socket)
            {
                var o = new AcceptOpts { ApiVersion = 1, Local = me, Remote = who, Socket = s };
                EOS_P2P_AcceptConnection(p2p, &o);
            }
        }

        internal static void Send(IntPtr to, byte[] d, bool reliable)
        {
            if (to == IntPtr.Zero) return;
            int r;
            fixed (SocketId* s = &socket)
            fixed (byte* data = d)
            {
                var o = new SendOpts { ApiVersion = 2, Local = me, Remote = to, Socket = s, Channel = Channel, Len = (uint)d.Length, Data = data, AllowDelayed = 1, Reliability = reliable ? 2 : 0 };
                r = EOS_P2P_SendPacket(p2p, &o);
            }
            if (r != ResultSuccess && sendFails++ < 5) Plugin.L.LogWarning($"net: epic send failed, result {r}");
            if (r != ResultSuccess && sendFails == 5 && !told) { told = true; Plugin.L.LogWarning("net: epic sends keep failing, more not logged"); }
        }

        internal static void Poll()
        {
            byte ch = Channel;
            var so = new SizeOpts { ApiVersion = 2, Local = me, Channel = &ch };
            var ro = new RecvOpts { ApiVersion = 2, Local = me, Max = (uint)buf.Length, Channel = &ch };
            for (int guard = 0; guard < 256; guard++)
            {
                uint size;
                if (EOS_P2P_GetNextReceivedPacketSize(p2p, &so, &size) != ResultSuccess) break;
                IntPtr peer; SocketId sock; byte gotCh; uint got;
                int r;
                fixed (byte* data = buf) r = EOS_P2P_ReceivePacket(p2p, &ro, &peer, &sock, &gotCh, data, &got);
                if (r != ResultSuccess) break;
                OnPacket(Transport.Epic, 0, IdString(peer), peer, buf, (int)got);
            }
        }
    }
}
