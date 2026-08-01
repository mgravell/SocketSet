# DRAFT issue for dotnet/aspnetcore — for review before filing

> This is a draft only. `gh` is not installed on the repro machine, so nothing has been sent.
> Suggested repo: **dotnet/aspnetcore**. Search first for existing issues mentioning
> `IBufferWriter` / `Advance` / transport `PipeWriter` before filing.

---

## Title

Kestrel writes response body into a transport `PipeWriter` buffer it has already `Advance`d past (no intervening `GetSpan`/`GetMemory`)

## Summary

When writing an HTTP/1.1 response, Kestrel calls the connection transport's `Transport.Output`
(`PipeWriter`) with the pattern:

```
GetSpan(0)         // one lease
Advance(N)         // status line + headers
Advance(M)         // response body  <-- no GetSpan/GetMemory since the previous Advance
FlushAsync
```

The body bytes (`M`) are written into the **same** buffer returned by the single `GetSpan(0)`, at the
offset *after* the `Advance(N)` that already consumed the header bytes. Per the `IBufferWriter<T>`
contract this is not allowed:

> `IBufferWriter<T>.Advance(int)`: *"You must request a new buffer after calling `Advance(Int32)` to
> continue writing more data; you cannot write to a previously acquired buffer."*
>
> `IBufferWriter<T>.GetMemory(int)` / `GetSpan(int)`: *"There is no guarantee that successive calls will
> return the same buffer or the same-sized buffer."*

`System.IO.Pipelines.Pipe` tolerates it because `GetSpan`/`GetMemory` return contiguous space within the
current segment, so writing past the earlier `Advance` still lands in valid, still-owned memory. But a
**conformant** `PipeWriter` whose `GetMemory` legitimately returns a *different* buffer on the next call —
which the second quote explicitly permits — never receives the body: those bytes go into the first
lease's buffer, and the second `Advance(M)` publishes `M` bytes of whatever the *new* buffer contained.

This makes it effectively impossible to back `Transport.Output` with a non-`Pipe` `PipeWriter`
(e.g. a segmented/pooled buffer over a custom connection transport) without special-casing this Kestrel
behavior.

## Repro

Minimal, self-contained (one `Program.cs`, ~230 lines; no third-party packages). It hosts `KestrelServer`
over a fake in-memory `IConnectionListenerFactory` that feeds one canned HTTP/1.1 request and exposes an
instrumented `PipeWriter` as `ConnectionContext.Transport.Output`. The writer logs every
`GetSpan`/`GetMemory`/`Advance`/`FlushAsync` call and flags any `Advance(>0)` with no preceding
`GetSpan`/`GetMemory`.

Endpoint under test:

```csharp
app.MapGet("/t", () => Results.Bytes(Encoding.ASCII.GetBytes("xxxxx"), "text/plain"));
```

Output:

```
[repro] runtime: .NET 10.0.10
=== Kestrel -> transport PipeWriter call sequence ===
  GetSpan(hint=0) -> 65536B at head=0
  Advance(137)  (wrote into the buffer leased at head=0)
  Advance(5)  *** no GetSpan/GetMemory since the last Advance -- wrote past the previous Advance ***
  FlushAsync  (head=142)

VERDICT: 1 Advance(>0) call(s) with NO preceding GetSpan/GetMemory.
```

(The repro uses a contiguous backing buffer so the assembled response is byte-correct; the point is the
call *pattern*, which is independent of the backing. A distinct-buffer-per-call writer drops the body.)

## Expected behavior

Kestrel writes to a transport `PipeWriter` using only the documented `IBufferWriter<T>` contract: each
`Advance(>0)` is preceded by its own `GetSpan`/`GetMemory`, and it does not continue writing into a buffer
after `Advance`-ing past it. (Equivalently: a single `GetSpan`/`GetMemory` covering the whole
headers+body write, then one `Advance` of the total, would also be conformant.)

## Actual behavior

A single `GetSpan(0)` lease is reused across two `Advance` calls; the second write (the body) goes into
the already-advanced region of the first lease with no new `GetSpan`/`GetMemory`.

## Versions

- Reproduces on **.NET 10.0.10** and **.NET 11.0.0-preview.6.26359.118** (same trace on both).
- SDK 10.0.302, Linux.

## Notes / questions for the team

1. Is this intended reliance on `Pipe`'s concrete contiguity, or a latent contract bug for custom
   connection transports?
2. If intended: the `IBufferWriter<T>` doc wording above is then misleading for `Transport.Output`, and it
   would help to document that a transport `PipeWriter` must return contiguous space across `Advance`
   within a flush (i.e. behave like `Pipe`).
3. Suspected origin: `Http1OutputProducer` writing the response headers (an `Advance`) and then the body
   into the same transport-writer memory without re-acquiring. Not verified against source line-by-line —
   flagged for maintainers.

## How it surfaced (context, optional)

Found while backing `Transport.Output` with a segmented pooled buffer (not `Pipe`) over a custom
connection transport: responses came out with their **body replaced by the response's own first bytes**
(`HTTP/`), because the custom writer relocated on the second `Advance` exactly as `IBufferWriter<T>`
allows. Working around it required re-establishing the write position on every `Advance`.
