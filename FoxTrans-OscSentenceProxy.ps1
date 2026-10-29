param(
    [int]$ListenPort = 9010,
    [int]$TargetPort = 9000,
    [string]$LogPath = "C:\VRChat\foxtrans-osc-sentence.log",
    [int]$FinalGraceMs = 500,
    [int]$DraftQuietMs = 0,
    [int]$PreviewCadenceMs = 1500
)

$ErrorActionPreference = "Stop"

$source = @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;

// This state machine has no I/O. Its clock is supplied by the caller so cadence,
// ordering and mute/drain behavior can be checked without opening OSC sockets.
public sealed class CapturedOscEmission
{
    public string Identity { get; internal set; }
    public string Text { get; internal set; }
    public int Revision { get; internal set; }
    public bool IsFinal { get; internal set; }
    public bool SendChat { get; internal set; }
    public bool ClearTyping { get; internal set; }
    internal long? ExpiresAt;
}

public sealed class CapturedOscGate
{
    private const int HistoryLimit = 256;
    private const int FinalQueueLimit = 64;
    private const long DrainWindowMs = 20000;
    private readonly int cadenceMs;
    private readonly HashSet<string> retiredPreviewIds = new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<string> retiredOrder = new Queue<string>();
    private readonly HashSet<string> finalIds = new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<string> finalOrder = new Queue<string>();
    private readonly Queue<CapturedOscEmission> finals = new Queue<CapturedOscEmission>();
    private string activeIdentity;
    private int latestRevision;
    private CapturedOscEmission preview;
    private long? allowanceUntil;
    private long? lastSubmittedAt;
    private string lastSubmittedIdentity;
    private string lastSubmittedText;

    public CapturedOscGate(int requestedCadenceMs) { cadenceMs = Math.Max(1500, requestedCadenceMs); Muted = true; }
    public bool Muted { get; private set; }
    public string LastDisposition { get; private set; }
    public int PendingFinalCount { get { return finals.Count; } }
    public int ExpiredFinalCount { get; private set; }
    public bool HasPendingPreview { get { return preview != null; } }
    public bool HasDrainAllowance(long nowMs) { return allowanceUntil.HasValue && nowMs <= allowanceUntil.Value; }

    public bool Start(string identity)
    {
        if (!Valid(identity, 512) || Muted || retiredPreviewIds.Contains(identity) || finalIds.Contains(identity))
        { LastDisposition = "discarded-stale-start"; return false; }
        if (String.Equals(activeIdentity, identity, StringComparison.Ordinal))
        { LastDisposition = "captured-start"; return true; }
        Retire(activeIdentity);
        activeIdentity = identity;
        latestRevision = 0;
        preview = null;
        LastDisposition = "captured-start";
        return true;
    }

    public bool OfferPreview(string identity, int revision, string text)
    {
        if (!Valid(identity, 512) || !Valid(text, 4096) || revision <= 0)
        { LastDisposition = "ignored-preview"; return false; }
        if (Muted)
        { LastDisposition = "discarded-muted-preview"; return false; }
        if (!String.Equals(activeIdentity, identity, StringComparison.Ordinal) ||
            retiredPreviewIds.Contains(identity) || finalIds.Contains(identity) || revision <= latestRevision)
        { LastDisposition = "discarded-stale-preview"; return false; }
        latestRevision = revision;
        if (String.Equals(lastSubmittedIdentity, identity, StringComparison.Ordinal) &&
            String.Equals(lastSubmittedText, text, StringComparison.Ordinal))
        { preview = null; LastDisposition = "duplicate-preview"; return false; }
        if (preview != null && String.Equals(preview.Text, text, StringComparison.Ordinal))
        { preview.Revision = revision; LastDisposition = "duplicate-preview"; return false; }
        preview = new CapturedOscEmission { Identity = identity, Revision = revision, Text = text };
        LastDisposition = "preview-buffered";
        return true;
    }

    public bool OfferFinal(string identity, string text, long nowMs)
    {
        if (!Valid(identity, 512) || !Valid(text, 4096))
        { LastDisposition = "ignored-final"; return false; }
        Retire(identity);
        if (preview != null && String.Equals(preview.Identity, identity, StringComparison.Ordinal)) preview = null;
        if (finalIds.Contains(identity))
        { LastDisposition = "duplicate-final"; return false; }
        if (Muted && !HasDrainAllowance(nowMs))
        { LastDisposition = "discarded-muted-final"; return false; }
        if (finals.Count >= FinalQueueLimit)
        { LastDisposition = "discarded-final-overflow"; return false; }
        Remember(finalIds, finalOrder, identity);
        finals.Enqueue(new CapturedOscEmission { Identity = identity, Text = text, IsFinal = true,
            ExpiresAt = Muted ? allowanceUntil : null });
        LastDisposition = "final-buffered";
        return true;
    }

    public void SetMuted(bool muted) { SetMuted(muted, 0); }
    public void SetMuted(bool muted, long nowMs)
    {
        bool changed = muted != Muted;
        if (changed) allowanceUntil = null;
        Muted = muted;
        if (muted)
        {
            Retire(activeIdentity);
            preview = null;
            if (changed)
                foreach (CapturedOscEmission value in finals)
                    value.ExpiresAt = value.ExpiresAt.HasValue
                        ? Math.Min(value.ExpiresAt.Value, nowMs + DrainWindowMs) : nowMs + DrainWindowMs;
        }
        // A final accepted just before mute stays blocked until the controller's
        // following drain=true/false packet, rather than racing that permission.
        // Keep the last active identity so a later reset can also tombstone a
        // pre-mute final that had not arrived yet, without blocking allowed drain.
    }

    public void SetDrain(bool allowed, long nowMs)
    {
        allowanceUntil = Muted && allowed ? (long?)(nowMs + DrainWindowMs) : null;
        if (Muted && !allowed) finals.Clear();
    }

    public void Reset()
    {
        if (!String.IsNullOrEmpty(activeIdentity)) Remember(finalIds, finalOrder, activeIdentity);
        Retire(activeIdentity);
        activeIdentity = null;
        preview = null;
        finals.Clear();
        allowanceUntil = null;
        latestRevision = 0;
        // Preserve recent identity tombstones and cadence across resets; delayed
        // packets must not revive an old preview or double-submit an old final.
    }

    public void Cancel(string identity)
    {
        if (!Valid(identity, 512)) return;
        Retire(identity);
        Remember(finalIds, finalOrder, identity);
        if (String.Equals(activeIdentity, identity, StringComparison.Ordinal)) activeIdentity = null;
        if (preview != null && String.Equals(preview.Identity, identity, StringComparison.Ordinal)) preview = null;
        int count = finals.Count;
        for (int i = 0; i < count; i++)
        {
            CapturedOscEmission value = finals.Dequeue();
            if (!String.Equals(value.Identity, identity, StringComparison.Ordinal)) finals.Enqueue(value);
        }
    }

    public void RecordLegacySubmission(long nowMs)
    {
        lastSubmittedAt = nowMs;
        lastSubmittedIdentity = null;
        lastSubmittedText = null;
    }

    public CapturedOscEmission Poll(long nowMs)
    {
        while (finals.Count > 0 && finals.Peek().ExpiresAt.HasValue && nowMs > finals.Peek().ExpiresAt.Value)
        { finals.Dequeue(); ExpiredFinalCount++; }
        if (finals.Count > 0)
        {
            if (Muted && !HasDrainAllowance(nowMs))
            {
                if (allowanceUntil.HasValue)
                { ExpiredFinalCount += finals.Count; finals.Clear(); allowanceUntil = null; }
                return null;
            }
            CapturedOscEmission next = finals.Peek();
            bool unchanged = String.Equals(lastSubmittedIdentity, next.Identity, StringComparison.Ordinal) &&
                String.Equals(lastSubmittedText, next.Text, StringComparison.Ordinal);
            // No extra grace is added when idle. Any changed chat text, preview
            // or final, shares the next safe slot after the previous submission.
            if (!unchanged && !SlotAvailable(nowMs)) return null;
            finals.Dequeue();
            next.SendChat = !unchanged;
            next.ClearTyping = Muted || activeIdentity == null ||
                String.Equals(activeIdentity, next.Identity, StringComparison.Ordinal);
            if (next.SendChat) RecordSubmission(next, nowMs);
            if (Muted) { allowanceUntil = null; finals.Clear(); }
            return next;
        }
        if (Muted || preview == null || !SlotAvailable(nowMs)) return null;
        CapturedOscEmission snapshot = preview;
        preview = null;
        snapshot.SendChat = true;
        RecordSubmission(snapshot, nowMs);
        return snapshot;
    }

    private bool SlotAvailable(long nowMs) { return !lastSubmittedAt.HasValue || nowMs - lastSubmittedAt.Value >= cadenceMs; }
    private void RecordSubmission(CapturedOscEmission value, long nowMs)
    {
        lastSubmittedAt = nowMs;
        lastSubmittedIdentity = value.Identity;
        lastSubmittedText = value.Text;
    }
    private void Retire(string identity)
    {
        if (!String.IsNullOrEmpty(identity)) Remember(retiredPreviewIds, retiredOrder, identity);
    }
    private static void Remember(HashSet<string> set, Queue<string> order, string identity)
    {
        if (!set.Add(identity)) return;
        order.Enqueue(identity);
        while (order.Count > HistoryLimit) set.Remove(order.Dequeue());
    }
    private static bool Valid(string value, int maximum) { return !String.IsNullOrWhiteSpace(value) && value.Length <= maximum; }
}

public static class FoxTransOscSentenceProxy
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    public static int Run(int listenPort, int targetPort, string logPath, int finalGraceMs, int draftQuietMs, int previewCadenceMs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath));
        using (var log = new StreamWriter(logPath, true, new UTF8Encoding(false)))
        using (var input = new UdpClient(new IPEndPoint(IPAddress.Loopback, listenPort)))
        using (var output = new UdpClient())
        {
            log.AutoFlush = true;
            input.Client.ReceiveTimeout = 100;
            output.Connect(IPAddress.Loopback, targetPort);
            WriteLog(log, "ready", "Listening on 127.0.0.1:" + listenPort +
                "; forwarding submitted translations to 127.0.0.1:" + targetPort +
                "; final grace " + finalGraceMs + " ms; preview cadence " + Math.Max(1500, previewCadenceMs) + " ms.");
            Console.WriteLine("FoxTrans OSC sentence mode is ready.");
            Console.WriteLine("FoxTrans -> 127.0.0.1:{0} -> VRChat 127.0.0.1:{1}", listenPort, targetPort);
            Console.WriteLine("Submitted previews never open the keyboard; final-only clients keep drafts hidden.");
            Console.WriteLine("Keep this window open. Press Ctrl+C to stop.");

            string latest = null;
            bool pending = false;
            DateTime? finalAt = null;
            bool providerTyping = true;
            bool muted = true;
            var captured = new CapturedOscGate(previewCadenceMs);
            var clock = Stopwatch.StartNew();
            var sender = new IPEndPoint(IPAddress.Any, 0);
            string flushNonce = null;
            IPEndPoint flushTarget = null;
            long flushRequestedAt = 0, lastPacketAt = 0;

            while (true)
            {
                if (!muted && input.Available == 0 && pending && finalAt.HasValue && DateTime.UtcNow >= finalAt.Value && !String.IsNullOrWhiteSpace(latest))
                {
                    Send(output, ChatboxInput(latest, true, false));
                    captured.RecordLegacySubmission(clock.ElapsedMilliseconds);
                    Send(output, Typing(false));
                    WriteLog(log, "final", latest);
                    pending = false;
                    latest = null;
                    finalAt = null;
                }

                if (input.Available == 0)
                {
                    int expiredBefore = captured.ExpiredFinalCount;
                    CapturedOscEmission emission = captured.Poll(clock.ElapsedMilliseconds);
                    if (captured.ExpiredFinalCount != expiredBefore)
                        WriteLog(log, "expired-captured-finals", (captured.ExpiredFinalCount - expiredBefore) + " queued muted final(s) expired.");
                    if (emission != null)
                    {
                        if (emission.SendChat) Send(output, ChatboxInput(emission.Text, true, false));
                        if (emission.ClearTyping) Send(output, Typing(false));
                        if (emission.IsFinal)
                            WriteLog(log, emission.SendChat ? "final-captured" : "final-captured-unchanged",
                                emission.SendChat ? emission.Text : emission.Identity);
                        else
                            WriteLog(log, "preview-captured", emission.Identity + " revision " + emission.Revision +
                                "; " + emission.Text.Length + " translated chars.");
                    }
                }

                if (flushNonce != null && input.Available == 0 && captured.PendingFinalCount == 0 && !pending &&
                    clock.ElapsedMilliseconds - flushRequestedAt >= 100 && clock.ElapsedMilliseconds - lastPacketAt >= 100)
                {
                    // Wait for queued finals to pass the normal OSC cadence and
                    // a short quiet receive boundary before allowing an update
                    // to stop this process. Never bypass mute/drain permission.
                    byte[] acknowledgment;
                    using (var stream = new MemoryStream())
                    {
                        PackString(stream, "/foxtrans/control/flushed");
                        PackString(stream, ",s"); PackString(stream, flushNonce);
                        acknowledgment = stream.ToArray();
                    }
                    input.Send(acknowledgment, acknowledgment.Length, flushTarget);
                    flushNonce = null; flushTarget = null;
                    WriteLog(log, "output-flushed", "Queued captured messages have finished normal output processing.");
                }

                byte[] packet;
                try { packet = input.Receive(ref sender); }
                catch (SocketException exception)
                {
                    if (exception.SocketErrorCode == SocketError.TimedOut) continue;
                    throw;
                }
                lastPacketAt = clock.ElapsedMilliseconds;
                int offset = 0;
                string address;
                string tags;
                if (!TryReadString(packet, ref offset, out address) ||
                    !TryReadString(packet, ref offset, out tags))
                {
                    WriteLog(log, "ignored", "Malformed OSC packet.");
                    continue;
                }

                if (address == "/foxtrans/control/flush")
                {
                    string nonce; Guid parsedNonce;
                    if (tags != ",s" || !TryReadString(packet, ref offset, out nonce) || !Guid.TryParse(nonce, out parsedNonce)) continue;
                    if (!String.Equals(flushNonce, nonce, StringComparison.Ordinal)) flushRequestedAt = clock.ElapsedMilliseconds;
                    flushNonce = nonce;
                    flushTarget = new IPEndPoint(sender.Address, sender.Port);
                    continue;
                }

                if (address == "/foxtrans/control/muted")
                {
                    if (tags != ",T" && tags != ",F") continue;
                    bool requestedMuted = tags.IndexOf('T', 1) >= 0;
                    bool muteStateChanged = requestedMuted != muted;
                    muted = requestedMuted;
                    captured.SetMuted(muted, clock.ElapsedMilliseconds);
                    if (muted)
                    {
                        latest = null;
                        pending = false;
                        finalAt = null;
                        providerTyping = true;
                        Send(output, Typing(false));
                    }
                    if (muteStateChanged)
                        WriteLog(log, "muted", muted ? "true; pending and late output blocked" : "false; output enabled");
                    continue;
                }

                if (address == "/foxtrans/control/drain")
                {
                    if (tags != ",T" && tags != ",F") continue;
                    // Only the controller can opt in to one already-captured final
                    // while muted. Ordinary late drafts remain blocked.
                    captured.SetDrain(tags.IndexOf('T', 1) >= 0, clock.ElapsedMilliseconds);
                    WriteLog(log, "drain", captured.HasDrainAllowance(clock.ElapsedMilliseconds)
                        ? "Allowing one completed pre-mute sentence for at most 20 seconds." : "Captured-final permission cleared.");
                    continue;
                }

                if (address == "/foxtrans/control/captured-start")
                {
                    string identity;
                    if (tags == ",s" && TryReadString(packet, ref offset, out identity))
                    {
                        captured.Start(identity);
                        WriteLog(log, captured.LastDisposition, identity);
                    }
                    continue;
                }

                if (address == "/foxtrans/control/captured-preview")
                {
                    string identity;
                    string text;
                    int revision;
                    if (tags == ",sis" && TryReadString(packet, ref offset, out identity) &&
                        TryReadInt32(packet, ref offset, out revision) && TryReadString(packet, ref offset, out text))
                    {
                        captured.OfferPreview(identity, revision, text);
                        WriteLog(log, captured.LastDisposition, identity + " revision " + revision + "; " + text.Length + " translated chars.");
                    }
                    continue;
                }

                if (address == "/foxtrans/control/captured-cancel")
                {
                    string identity;
                    if (tags == ",s" && TryReadString(packet, ref offset, out identity))
                    {
                        captured.Cancel(identity);
                        WriteLog(log, "captured-cancel", identity);
                    }
                    continue;
                }

                if (address == "/foxtrans/control/captured-final")
                {
                    string identity;
                    string text;
                    if (tags != ",ss" || !TryReadString(packet, ref offset, out identity) || String.IsNullOrWhiteSpace(identity) ||
                        !TryReadString(packet, ref offset, out text) || String.IsNullOrWhiteSpace(text)) continue;
                    bool accepted = captured.OfferFinal(identity, text, clock.ElapsedMilliseconds);
                    WriteLog(log, captured.LastDisposition, identity);
                    if (!accepted) continue;
                    latest = null;
                    pending = false;
                    finalAt = null;
                    providerTyping = true;
                    continue;
                }

                if (address == "/foxtrans/control/reset")
                {
                    captured.Reset();
                    latest = null;
                    pending = false;
                    finalAt = null;
                    providerTyping = true;
                    Send(output, Typing(false));
                    WriteLog(log, "reset", "Discarded pending translation and cleared typing.");
                    continue;
                }

                if (muted && (address == "/chatbox/input" || address == "/chatbox/typing"))
                {
                    WriteLog(log, "discarded-muted", address);
                    continue;
                }

                if (address == "/chatbox/input" && tags.StartsWith(",s", StringComparison.Ordinal))
                {
                    string text;
                    if (!TryReadString(packet, ref offset, out text) || String.IsNullOrWhiteSpace(text))
                        continue;

                    latest = text;
                    pending = true;
                    // A late LLM result after typing=false is still a completed sentence.
                    // Do not erase its completion timer while waiting for a second false event.
                    finalAt = providerTyping && draftQuietMs <= 0 ? (DateTime?)null :
                        DateTime.UtcNow.AddMilliseconds(providerTyping
                            ? Math.Max(finalGraceMs, draftQuietMs) : Math.Max(0, finalGraceMs));
                    WriteLog(log, "buffered", text);
                    continue;
                }

                if (address == "/chatbox/typing")
                {
                    bool isTyping = tags.IndexOf('T', 1) >= 0;
                    providerTyping = isTyping;
                    if (!isTyping && pending && !String.IsNullOrWhiteSpace(latest))
                    {
                        finalAt = DateTime.UtcNow.AddMilliseconds(Math.Max(0, finalGraceMs));
                        WriteLog(log, "settling", "Waiting for confirmation pause before final send.");
                        continue;
                    }
                    if (isTyping) finalAt = null;
                    Send(output, Typing(isTyping));
                    WriteLog(log, "typing", isTyping ? "true" : "false");
                    continue;
                }

                Send(output, packet);
            }
        }
    }

    private static void Send(UdpClient client, byte[] packet)
    {
        client.Send(packet, packet.Length);
    }

    private static byte[] ChatboxInput(string text, bool sendImmediately, bool notify)
    {
        using (var stream = new MemoryStream())
        {
            PackString(stream, "/chatbox/input");
            PackString(stream, ",s" + (sendImmediately ? "T" : "F") + (notify ? "T" : "F"));
            PackString(stream, text);
            return stream.ToArray();
        }
    }

    private static byte[] Typing(bool isTyping)
    {
        using (var stream = new MemoryStream())
        {
            PackString(stream, "/chatbox/typing");
            PackString(stream, isTyping ? ",T" : ",F");
            return stream.ToArray();
        }
    }

    private static void PackString(Stream stream, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
        while ((stream.Length & 3) != 0)
            stream.WriteByte(0);
    }

    private static bool TryReadString(byte[] packet, ref int offset, out string value)
    {
        value = null;
        if (offset < 0 || offset >= packet.Length)
            return false;
        int end = offset;
        while (end < packet.Length && packet[end] != 0)
            end++;
        if (end >= packet.Length)
            return false;
        try
        {
            value = Utf8.GetString(packet, offset, end - offset);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        offset = (end + 4) & ~3;
        return offset <= packet.Length;
    }

    private static bool TryReadInt32(byte[] packet, ref int offset, out int value)
    {
        value = 0;
        if (offset < 0 || packet.Length - offset < 4) return false;
        value = (packet[offset] << 24) | (packet[offset + 1] << 16) | (packet[offset + 2] << 8) | packet[offset + 3];
        offset += 4;
        return true;
    }

    private static void WriteLog(StreamWriter log, string kind, string message)
    {
        string safe = (message ?? "").Replace("\r", " ").Replace("\n", " ");
        log.WriteLine("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}: {2}", DateTime.Now, kind, safe);
    }
}
'@

try {
    Add-Type -TypeDefinition $source -Language CSharp
    exit [FoxTransOscSentenceProxy]::Run($ListenPort, $TargetPort, $LogPath, $FinalGraceMs, $DraftQuietMs, $PreviewCadenceMs)
}
catch {
    $message = $_.Exception.ToString()
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Add-Content -LiteralPath $LogPath -Value "[$timestamp] fatal: $message"
    Write-Host "FoxTrans OSC sentence mode failed:" -ForegroundColor Red
    Write-Host $message
    exit 1
}
