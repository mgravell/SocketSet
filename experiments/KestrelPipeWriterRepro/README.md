# Kestrel writes past an `Advance` into a previously-acquired transport buffer

Minimal, self-contained repro (one file, `Program.cs`; no SocketSet, no CycleBuffer, no external
transport). It hosts `KestrelServer` over a fake in-memory connection transport that feeds one canned
HTTP/1.1 request and exposes an instrumented `PipeWriter` as `ConnectionContext.Transport.Output`. The
writer logs every `GetSpan`/`GetMemory`/`Advance`/`FlushAsync` call.

## Run

```
dotnet run -c Release
```

## What it shows

For a minimal-API endpoint returning a 5-byte body, Kestrel's calls to the transport `PipeWriter` are:

```
GetSpan(hint=0) -> 65536B at head=0
Advance(137)   (status line + headers)
Advance(5)     *** no GetSpan/GetMemory since the last Advance ***   (the response body)
FlushAsync
```

A single `GetSpan`, then **two** `Advance` calls. Between them there is no new `GetSpan`/`GetMemory`:
Kestrel writes the response body into the *same* buffer it already `Advance(137)`-d past, then
`Advance(5)`s it.

## Why that is a contract problem

`IBufferWriter<T>.Advance(int)` (the base of `PipeWriter`) documents:

> You must request a new buffer after calling `Advance(Int32)` to continue writing more data; you cannot
> write to a previously acquired buffer.

and for `GetMemory`/`GetSpan`:

> There is no guarantee that successive calls will return the same buffer or the same-sized buffer.

`System.IO.Pipelines.Pipe` happens to return contiguous space within its current segment, so writing past
an `Advance` into the same buffer works *for that implementation*. But a conformant `PipeWriter` whose
`GetMemory` returns a **different** buffer per call — legal per the second quote — never sees the body
bytes: they land in the buffer from the first lease, and the second `Advance(5)` publishes 5 bytes of
whatever the new buffer happened to contain. That is exactly how this surfaced: a custom transport
`PipeWriter` (backed by a segmented pooled buffer) emitted a response whose body was the response's own
first 5 header bytes (`HTTP/`), because the writer relocated on the second `Advance` as the contract
allows.

The repro here uses a contiguous backing so the assembled response is byte-correct — the point is purely
the **call pattern** (`VERDICT: 1 Advance(>0) call with no preceding GetSpan/GetMemory`), which is
independent of the backing.

## Source

Suspected origin is `Http1OutputProducer` writing response headers (an `Advance`) and then the body into
the same transport-writer memory without re-acquiring. Reproduced on .NET 10 / ASP.NET Core 10.
