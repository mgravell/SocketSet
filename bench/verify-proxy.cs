#:property TargetFramework=net10.0
#:property PublishAot=false
#:property IsPackable=false
// ^ This file lives inside the repo, so it inherits Directory.Build.props — which multi-targets
// (net10.0;net472). A file-based app defaults to AOT, and AOT cannot target net472, so without these
// it fails with NETSDK1207 for a reason that has nothing to do with this file.
//
// Correctness gate for a RESP proxy leg. Raw sockets, raw RESP, NO client library on purpose:
// a client library brings its own handshake (HELLO/INFO/CONFIG), its own multiplexing and its own
// reconnect logic, so a failure could belong to any of them. This isolates the proxy.
//
// Run:  dotnet run bench/verify-proxy.cs -- <host> <port> [label]
// Exit: 0 = all PASS, 1 = any FAIL.
//
// WHAT EACH TEST IS FOR (a rig that cannot say this is a rig you cannot trust):
//   1 ping            — plumbing. Proves nothing else.
//   2 roundtrip       — SET/GET byte-exact at 1 B .. 1 MB. Catches framing/chunking/large-value bugs,
//                       which is where a transport swap actually breaks: multi-segment reads, buffer
//                       boundaries, partial writes.
//   3 pipeline        — N commands written WITHOUT waiting, responses verified IN ORDER. A proxy that
//                       reorders under depth passes every request/response test and fails this.
//   4 mixed-pipeline  — THE IMPORTANT ONE. Interleaves LOCAL commands (PING, which the proxy answers
//                       itself) with FORWARDED ones (GET, which go upstream) in a single pipelined
//                       burst. A locally-generated reply that is not sequenced against in-flight
//                       upstream replies lands in the wrong slot. This is exactly the bug class the
//                       proxy already fixed once (commit dbd4ad4d "fix local responses being
//                       out-of-band"), and exactly what a fast-reply optimisation would reintroduce.
//   5 concurrent      — many connections at once, each verifying its own values. Catches cross-talk
//                       between clients sharing an upstream leg.
using System.Buffers;
using System.Net.Sockets;
using System.Text;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 6379;
string label = args.Length > 2 ? args[2] : $"{host}:{port}";

int failures = 0;
void Report(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name,-22} {detail}");
    if (!ok) failures++;
}

Console.WriteLine($"=== verify-proxy: {label} ===");

// --- 1: ping -----------------------------------------------------------------------------------
try
{
    using var c = new RespConn(host, port);
    c.Send(["PING"]);
    var r = c.Read();
    Report("ping", r is "+PONG", r ?? "(null)");
}
catch (Exception ex) { Report("ping", false, ex.Message); }

// --- 2: byte-exact round-trip across sizes -----------------------------------------------------
// Sizes straddle every buffer boundary that matters: the 4 KB default page, the 64 KB page, and a
// 1 MB value that must span many segments in either direction.
int[] sizes = [1, 64, 1024, 4096, 4097, 65536, 262144, 1048576];
try
{
    using var c = new RespConn(host, port);
    foreach (var size in sizes)
    {
        // Non-repeating payload: a constant byte would hide an off-by-N that a varying one exposes.
        var payload = new byte[size];
        for (int i = 0; i < size; i++) payload[i] = (byte)(i * 31 + 7);
        string key = $"vp:rt:{size}";
        c.Send(["SET", key], payload);
        var set = c.Read();
        c.Send(["GET", key]);
        var got = c.ReadBulk();
        bool ok = set is "+OK" && got is not null && got.AsSpan().SequenceEqual(payload);
        Report($"roundtrip {size}B", ok,
            ok ? "" : $"set={set} len={(got?.Length.ToString() ?? "null")} want={size}");
    }
}
catch (Exception ex) { Report("roundtrip", false, ex.Message); }

// --- 3: deep pipeline, responses must arrive in order ------------------------------------------
try
{
    using var c = new RespConn(host, port);
    const int depth = 512;
    for (int i = 0; i < depth; i++) c.Send(["SET", $"vp:pl:{i}"], Encoding.ASCII.GetBytes($"v{i}"));
    c.Flush();
    bool allOk = true;
    for (int i = 0; i < depth; i++) if (c.Read() is not "+OK") { allOk = false; break; }
    for (int i = 0; i < depth; i++) c.Send(["GET", $"vp:pl:{i}"]);
    c.Flush();
    int firstBad = -1;
    for (int i = 0; i < depth; i++)
    {
        var v = c.ReadBulk();
        if (v is null || Encoding.ASCII.GetString(v) != $"v{i}") { firstBad = i; break; }
    }
    Report($"pipeline x{depth}", allOk && firstBad < 0,
        firstBad >= 0 ? $"first out-of-order/incorrect reply at index {firstBad}" : "");
}
catch (Exception ex) { Report("pipeline", false, ex.Message); }

// --- 4: local and forwarded commands interleaved in ONE pipelined burst -------------------------
try
{
    using var c = new RespConn(host, port);
    const int rounds = 256;
    c.Send(["SET", "vp:mix"], "sentinel"u8.ToArray());
    c.Flush();
    if (c.Read() is not "+OK") throw new Exception("setup SET failed");

    // PING is answered by the proxy itself; GET must go upstream. Alternating them means any reply the
    // proxy generates locally has to be sequenced behind the upstream replies already outstanding.
    for (int i = 0; i < rounds; i++) { c.Send(["PING"]); c.Send(["GET", "vp:mix"]); }
    c.Flush();
    int bad = -1;
    for (int i = 0; i < rounds; i++)
    {
        if (c.Read() is not "+PONG") { bad = i * 2; break; }
        var v = c.ReadBulk();
        if (v is null || !v.AsSpan().SequenceEqual("sentinel"u8)) { bad = i * 2 + 1; break; }
    }
    Report($"mixed-pipeline x{rounds}", bad < 0,
        bad >= 0 ? $"reply {bad} out of sequence — a LOCAL reply overtook an upstream one" : "");
}
catch (Exception ex) { Report("mixed-pipeline", false, ex.Message); }

// --- 4b: HELLO must be answered LOCALLY with an error, and must not disturb the connection -------
// A multiplexed proxy cannot forward HELLO (it would flip the shared upstream leg's protocol for every
// client on it). The compatible behaviour is a local -NOPROTO: clients treat any HELLO error as "RESP2
// server" and downgrade. This cell also guards the regression where HELLO handling breaks the SAME
// connection for subsequent commands.
try
{
    using var c = new RespConn(host, port);
    c.Send(["HELLO", "3"]);
    var r = c.Read();
    // Two legitimate outcomes: a RESP2-only endpoint refuses with an error (-NOPROTO; the multiplexing
    // proxy MUST do this), while a RESP3-capable server (Garnet, Redis 6+) answers a map. Either way the
    // REAL assertion is the connection still works afterwards -- HELLO handling must never wedge it.
    bool sane = r is not null && (r.StartsWith('-') || r.StartsWith('%') || r.StartsWith('*'));
    c.Send(["PING"]);
    var after = c.Read();
    Report("hello-then-usable", sane && after is "+PONG",
        $"hello={r ?? "(null)"} then ping={after ?? "(null)"}");
}
catch (Exception ex) { Report("hello-then-usable", false, ex.Message); }

// --- 5: concurrent connections -----------------------------------------------------------------
try
{
    const int clients = 32, opsEach = 64;
    var results = new bool[clients];
    await Parallel.ForAsync(0, clients, (ci, _) =>
    {
        try
        {
            using var c = new RespConn(host, port);
            for (int i = 0; i < opsEach; i++)
            {
                var val = Encoding.ASCII.GetBytes($"c{ci}-{i}");
                c.Send(["SET", $"vp:cc:{ci}:{i}"], val);
                c.Flush();
                if (c.Read() is not "+OK") return ValueTask.CompletedTask;
            }
            for (int i = 0; i < opsEach; i++)
            {
                c.Send(["GET", $"vp:cc:{ci}:{i}"]);
                c.Flush();
                var v = c.ReadBulk();
                if (v is null || Encoding.ASCII.GetString(v) != $"c{ci}-{i}") return ValueTask.CompletedTask;
            }
            results[ci] = true;
        }
        catch { /* leave false */ }
        return ValueTask.CompletedTask;
    });
    int okCount = results.Count(x => x);
    Report($"concurrent x{clients}", okCount == clients, $"{okCount}/{clients} clients clean");
}
catch (Exception ex) { Report("concurrent", false, ex.Message); }

Console.WriteLine(failures == 0 ? $"=== {label}: ALL PASS ===" : $"=== {label}: {failures} FAILURE(S) ===");
return failures == 0 ? 0 : 1;

/// <summary>Minimal blocking RESP codec: enough to issue commands and read every reply type.</summary>
sealed class RespConn : IDisposable
{
    private readonly Socket _sock;
    private readonly NetworkStream _ns;
    private readonly BufferedStream _out;
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _pos, _len;

    public RespConn(string host, int port)
    {
        _sock = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _sock.Connect(host, port);
        _ns = new NetworkStream(_sock, ownsSocket: false);
        _out = new BufferedStream(_ns, 128 * 1024);
    }

    public void Send(string[] parts, byte[]? trailing = null)
    {
        int n = parts.Length + (trailing is null ? 0 : 1);
        Write($"*{n}\r\n");
        foreach (var p in parts) { Write($"${Encoding.UTF8.GetByteCount(p)}\r\n"); Write(p); Write("\r\n"); }
        if (trailing is not null) { Write($"${trailing.Length}\r\n"); _out.Write(trailing); Write("\r\n"); }
        // Callers that pipeline call Flush() explicitly; a lone Send is flushed on the first Read.
    }

    private void Write(string s) { var b = Encoding.UTF8.GetBytes(s); _out.Write(b, 0, b.Length); }
    public void Flush() => _out.Flush();

    private int ReadByteRaw()
    {
        if (_pos >= _len)
        {
            _out.Flush(); // never block on a read while a request is still sitting in the write buffer
            _len = _ns.Read(_buf, 0, _buf.Length);
            _pos = 0;
            if (_len <= 0) throw new EndOfStreamException("connection closed by peer");
        }
        return _buf[_pos++];
    }

    private string ReadLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = ReadByteRaw();
            if (b == '\r') { ReadByteRaw(); return sb.ToString(); }
            sb.Append((char)b);
        }
    }

    /// <summary>Reads one reply, returning its textual form (bulk payloads are returned as length markers).</summary>
    public string? Read()
    {
        int prefix = ReadByteRaw();
        switch (prefix)
        {
            case '+': return "+" + ReadLine();
            case '-': return "-" + ReadLine();
            case ':': return ":" + ReadLine();
            case '$':
                {
                    int len = int.Parse(ReadLine());
                    if (len < 0) return "$-1";
                    Skip(len + 2);
                    return $"${len}";
                }
            case '*':
                {
                    int count = int.Parse(ReadLine());
                    for (int i = 0; i < count; i++) Read();
                    return $"*{count}";
                }
            // RESP3 replies -- a HELLO 3 against a RESP3-capable server (Garnet, Redis 6+) answers a map,
            // and a harness that cannot read one reports a healthy server as a protocol error.
            case '%':
                {
                    int pairs = int.Parse(ReadLine());
                    for (int i = 0; i < 2 * pairs; i++) Read();
                    return $"%{pairs}";
                }
            case '~':
                {
                    int count = int.Parse(ReadLine());
                    for (int i = 0; i < count; i++) Read();
                    return $"~{count}";
                }
            case ',': return "," + ReadLine();
            case '#': return "#" + ReadLine();
            case '(': return "(" + ReadLine();
            case '_': ReadLine(); return "_";
            default: throw new InvalidDataException($"unexpected RESP prefix 0x{prefix:x2}");
        }
    }

    /// <summary>Reads one bulk-string reply and returns its bytes (null for $-1).</summary>
    public byte[]? ReadBulk()
    {
        int prefix = ReadByteRaw();
        if (prefix == '-') throw new Exception("server error: " + ReadLine());
        if (prefix != '$') throw new InvalidDataException($"expected bulk string, got 0x{prefix:x2}");
        int len = int.Parse(ReadLine());
        if (len < 0) return null;
        var result = new byte[len];
        int got = 0;
        while (got < len)
        {
            if (_pos >= _len)
            {
                _out.Flush();
                _len = _ns.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len <= 0) throw new EndOfStreamException("closed mid-bulk");
            }
            int take = Math.Min(len - got, _len - _pos);
            Buffer.BlockCopy(_buf, _pos, result, got, take);
            _pos += take; got += take;
        }
        Skip(2); // trailing CRLF
        return result;
    }

    private void Skip(int count) { for (int i = 0; i < count; i++) ReadByteRaw(); }

    public void Dispose() { try { _out.Flush(); } catch { } _ns.Dispose(); _sock.Dispose(); }
}
