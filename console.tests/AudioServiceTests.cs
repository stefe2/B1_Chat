using System.IO;
using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Tests;

/// <summary>
/// SEQ-H05: audio probe, playback lifecycle and waveform cache. These drive the policy through
/// the media-handle seam, so no codec, no dispatcher and no real playback is involved — the one
/// exception is <see cref="AudioCodecSmokeTests"/> at the bottom, which decodes a real file.
/// </summary>
public class AudioProbeTests
{
    private static string TempFile(string extension = ".mp3")
    {
        var path = Path.Combine(Path.GetTempPath(), $"b1-audio-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, "not really audio");
        return path;
    }

    [Fact]
    public async Task Probe_returns_duration_on_success()
    {
        var factory = new FakeMediaHandleFactory(handle =>
        {
            handle.NaturalDurationMs = 4321;
            handle.RaiseOpened();
        });
        var probe = new AudioProbe(factory, fileExists: _ => true);

        var result = await probe.ProbeAsync("song.mp3");

        Assert.Equal(AudioProbeStatus.Ok, result.Status);
        Assert.True(result.Ok);
        Assert.Equal(4321, result.DurationMs);
        Assert.True(factory.Created.Single().Disposed);
    }

    [Fact]
    public async Task Probe_reports_missing_file_without_creating_a_handle()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        var probe = new AudioProbe(factory, fileExists: _ => false);

        var result = await probe.ProbeAsync("gone.mp3");

        Assert.Equal(AudioProbeStatus.FileMissing, result.Status);
        Assert.Equal(0, result.DurationMs);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task Probe_reports_empty_path_as_missing()
    {
        var probe = new AudioProbe(new FakeMediaHandleFactory(_ => { }), fileExists: _ => true);

        Assert.Equal(AudioProbeStatus.FileMissing, (await probe.ProbeAsync(null)).Status);
        Assert.Equal(AudioProbeStatus.FileMissing, (await probe.ProbeAsync("   ")).Status);
    }

    [Fact]
    public async Task Probe_reports_decode_failure_with_its_message()
    {
        var factory = new FakeMediaHandleFactory(handle => handle.RaiseFailed("codec missing"));
        var probe = new AudioProbe(factory, fileExists: _ => true);

        var result = await probe.ProbeAsync("broken.mp3");

        Assert.Equal(AudioProbeStatus.DecodeFailed, result.Status);
        Assert.Equal("codec missing", result.Message);
        Assert.True(factory.Created.Single().Disposed);
    }

    [Fact]
    public async Task Probe_treats_a_source_without_timespan_as_a_decode_failure()
    {
        var factory = new FakeMediaHandleFactory(handle =>
        {
            handle.NaturalDurationMs = null;
            handle.RaiseOpened();
        });
        var probe = new AudioProbe(factory, fileExists: _ => true);

        var result = await probe.ProbeAsync("stream.mp3");

        Assert.Equal(AudioProbeStatus.DecodeFailed, result.Status);
        Assert.Contains("codec", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_accepts_a_valid_zero_length_file()
    {
        // A real but empty file is a success at 0 ms, not a failure: the timeline still gives it
        // a minimum width, but it carries no warning badge.
        var factory = new FakeMediaHandleFactory(handle =>
        {
            handle.NaturalDurationMs = 0;
            handle.RaiseOpened();
        });
        var probe = new AudioProbe(factory, fileExists: _ => true);

        var result = await probe.ProbeAsync("silence.wav");

        Assert.Equal(AudioProbeStatus.Ok, result.Status);
        Assert.Equal(0, result.DurationMs);
    }

    [Fact]
    public async Task Probe_times_out_and_still_disposes_the_handle()
    {
        // The handle never answers — the defect this replaces left the task pending forever.
        var factory = new FakeMediaHandleFactory(_ => { });
        var probe = new AudioProbe(factory, timeoutMs: 40, fileExists: _ => true);

        var result = await probe.ProbeAsync("hangs.mp3");

        Assert.Equal(AudioProbeStatus.Timeout, result.Status);
        Assert.Equal(0, result.DurationMs);
        Assert.True(factory.Created.Single().Disposed);
    }

    [Fact]
    public async Task Probe_reports_cancellation()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        var probe = new AudioProbe(factory, timeoutMs: 10_000, fileExists: _ => true);
        using var cancellation = new CancellationTokenSource();

        var pending = probe.ProbeAsync("slow.mp3", cancellation.Token);
        cancellation.Cancel();
        var result = await pending;

        Assert.Equal(AudioProbeStatus.Cancelled, result.Status);
        Assert.True(factory.Created.Single().Disposed);
    }

    [Fact]
    public async Task Probe_reports_cancellation_requested_before_it_starts()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        var probe = new AudioProbe(factory, fileExists: _ => true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await probe.ProbeAsync("song.mp3", cancellation.Token);

        Assert.Equal(AudioProbeStatus.Cancelled, result.Status);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task Probe_converts_an_open_exception_into_a_typed_failure()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        factory.OpenThrows = new InvalidOperationException("bad uri");
        var probe = new AudioProbe(factory, fileExists: _ => true);

        var result = await probe.ProbeAsync("weird.mp3");

        Assert.Equal(AudioProbeStatus.DecodeFailed, result.Status);
        Assert.Equal("bad uri", result.Message);
        Assert.True(factory.Created.Single().Disposed);
    }

    [Fact]
    public async Task Probe_of_a_file_removed_after_the_check_fails_cleanly()
    {
        var path = TempFile();
        var factory = new FakeMediaHandleFactory(handle => handle.RaiseFailed("file vanished"));
        var probe = new AudioProbe(factory);
        File.Delete(path);

        // Existence is checked first, so a file already gone reports FileMissing…
        Assert.Equal(AudioProbeStatus.FileMissing, (await probe.ProbeAsync(path)).Status);

        // …and one that disappears between the check and the open surfaces the media error.
        var racing = new AudioProbe(factory, fileExists: _ => true);
        var result = await racing.ProbeAsync(path);
        Assert.Equal(AudioProbeStatus.DecodeFailed, result.Status);
    }
}

public class AudioPlaybackLifecycleTests
{
    private static AudioPlaybackService Build(FakeMediaHandleFactory factory) =>
        new(factory, fileExists: _ => true);

    [Fact]
    public void Play_opens_and_starts_the_clip()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = Build(factory);

        service.Play("a.mp3", loop: false, clipId: 7);

        var handle = factory.Created.Single();
        Assert.Equal("a.mp3", handle.OpenedPath);
        Assert.Equal(1, handle.PlayCount);
        Assert.Equal(1, service.ActiveCount);
    }

    [Fact]
    public void A_finished_non_looping_clip_leaves_the_active_set_and_closes()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = Build(factory);
        service.Play("a.mp3");

        factory.Created.Single().RaiseEnded();

        Assert.Equal(0, service.ActiveCount);
        Assert.True(factory.Created.Single().Disposed);
    }

    [Fact]
    public void A_looping_clip_restarts_and_stays_tracked()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = Build(factory);
        service.Play("loop.mp3", loop: true);

        var handle = factory.Created.Single();
        handle.RaiseEnded();

        Assert.Equal(1, handle.RewindCount);
        Assert.Equal(2, handle.PlayCount);
        Assert.Equal(1, service.ActiveCount);
        Assert.False(handle.Disposed);
    }

    [Fact]
    public void Resume_does_not_restart_a_clip_that_already_ended()
    {
        // The defect this covers: ended players stayed in the set, so Resume called Play on them
        // and they started over from zero, mid-pass.
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = Build(factory);
        service.Play("first.mp3");
        service.Play("second.mp3");
        var first = factory.Created[0];
        var second = factory.Created[1];

        first.RaiseEnded();
        service.PauseAll();
        service.ResumeAll();

        Assert.Equal(1, first.PlayCount);   // never played again
        Assert.Equal(0, first.PauseCount);  // and never paused after ending
        Assert.Equal(2, second.PlayCount);  // initial start + resume
        Assert.Equal(1, second.PauseCount);
    }

    [Fact]
    public void Resume_without_a_preceding_pause_does_nothing()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = Build(factory);
        service.Play("a.mp3");

        service.ResumeAll();

        Assert.Equal(1, factory.Created.Single().PlayCount);
    }

    [Fact]
    public void A_failing_clip_is_reported_once_and_retired()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = Build(factory);
        var failures = new List<AudioPlaybackFailure>();
        service.PlaybackFailed += failures.Add;
        service.Play(@"C:\music\bad.mp3", loop: false, clipId: 3);

        var handle = factory.Created.Single();
        handle.RaiseFailed("decoder exploded");
        handle.RaiseFailed("decoder exploded again"); // already retired: not reported twice

        var failure = Assert.Single(failures);
        Assert.Equal(3, failure.ClipId);
        Assert.Equal("bad.mp3", failure.FileName);
        Assert.Equal("decoder exploded", failure.Message);
        Assert.Equal(0, service.ActiveCount);
        Assert.True(handle.Disposed);
    }

    [Fact]
    public void A_missing_file_is_reported_without_creating_a_handle()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = new AudioPlaybackService(factory, fileExists: _ => false);
        var failures = new List<AudioPlaybackFailure>();
        service.PlaybackFailed += failures.Add;

        service.Play(@"C:\music\gone.mp3", loop: false, clipId: 9);

        Assert.Single(failures);
        Assert.Equal(9, failures[0].ClipId);
        Assert.Empty(factory.Created);
        Assert.Equal(0, service.ActiveCount);
    }

    [Fact]
    public void An_exception_while_starting_is_reported_as_a_failure()
    {
        var factory = new FakeMediaHandleFactory(_ => { }) { OpenThrows = new IOException("locked") };
        using var service = Build(factory);
        var failures = new List<AudioPlaybackFailure>();
        service.PlaybackFailed += failures.Add;

        service.Play("a.mp3");

        Assert.Equal("locked", Assert.Single(failures).Message);
        Assert.Equal(0, service.ActiveCount);
    }

    [Fact]
    public void Concurrent_clips_are_tracked_independently()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = Build(factory);
        service.Play("a.mp3");
        service.Play("b.mp3", loop: true);
        service.Play("c.mp3");

        Assert.Equal(3, service.ActiveCount);
        factory.Created[0].RaiseEnded();
        Assert.Equal(2, service.ActiveCount);
        factory.Created[1].RaiseEnded(); // looping: stays
        Assert.Equal(2, service.ActiveCount);
    }

    [Fact]
    public void StopAll_is_idempotent_and_closes_everything()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = Build(factory);
        service.Play("a.mp3");
        service.Play("b.mp3", loop: true);

        service.StopAll();
        service.StopAll();

        Assert.Equal(0, service.ActiveCount);
        Assert.All(factory.Created, handle => Assert.True(handle.Disposed));
        Assert.All(factory.Created, handle => Assert.Equal(1, handle.StopCount));
    }

    [Fact]
    public void Pause_then_stop_then_resume_starts_nothing()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = Build(factory);
        service.Play("a.mp3");

        service.PauseAll();
        service.StopAll();
        service.ResumeAll();

        Assert.Equal(0, service.ActiveCount);
        Assert.Equal(1, factory.Created.Single().PlayCount);
    }

    [Fact]
    public void Empty_path_is_ignored()
    {
        var factory = new FakeMediaHandleFactory(_ => { });
        using var service = Build(factory);
        var failures = new List<AudioPlaybackFailure>();
        service.PlaybackFailed += failures.Add;

        service.Play(null);
        service.Play("");

        Assert.Empty(factory.Created);
        Assert.Empty(failures);
    }
}

public class WaveformCacheTests
{
    private static readonly float[] Envelope = { 0.1f, 0.5f, 1f };

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"b1-wave-{Guid.NewGuid():N}.wav");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task The_same_file_is_decoded_once()
    {
        var path = WriteTemp("first");
        var calls = 0;
        var service = new WaveformService(decoder: (_, _) => { calls++; return Envelope; });

        var first = await service.GetPeaksAsync(path);
        var second = await service.GetPeaksAsync(path);

        Assert.Same(first, second);
        Assert.Equal(1, calls);
        File.Delete(path);
    }

    [Fact]
    public async Task Replacing_the_contents_of_the_same_path_invalidates_the_cache()
    {
        // The defect this covers: the old cache was keyed on the path alone, so editing a file in
        // place kept showing the previous envelope for the rest of the session.
        var path = WriteTemp("first");
        var calls = 0;
        var service = new WaveformService(decoder: (_, _) => { calls++; return new[] { (float)calls }; });

        await service.GetPeaksAsync(path);
        File.WriteAllText(path, "a longer, different payload");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        var second = await service.GetPeaksAsync(path);

        Assert.Equal(2, calls);
        Assert.Equal(2f, second![0]);
        File.Delete(path);
    }

    [Fact]
    public async Task A_missing_file_returns_null_and_is_not_cached()
    {
        var calls = 0;
        var service = new WaveformService(decoder: (_, _) => { calls++; return Envelope; });

        Assert.Null(await service.GetPeaksAsync(Path.Combine(Path.GetTempPath(), "nope-b1.wav")));
        Assert.Equal(0, calls);
        Assert.Equal(0, service.CachedCount);
    }

    [Fact]
    public async Task A_file_created_after_a_failed_attempt_can_still_be_decoded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"b1-wave-{Guid.NewGuid():N}.wav");
        var service = new WaveformService(decoder: (_, _) => Envelope);

        Assert.Null(await service.GetPeaksAsync(path));
        File.WriteAllText(path, "now it exists");
        Assert.Same(Envelope, await service.GetPeaksAsync(path));

        File.Delete(path);
    }

    [Fact]
    public async Task A_failed_decode_is_not_memoized()
    {
        var path = WriteTemp("content");
        var calls = 0;
        var service = new WaveformService(decoder: (_, _) => ++calls == 1 ? null : Envelope);

        Assert.Null(await service.GetPeaksAsync(path));
        Assert.Same(Envelope, await service.GetPeaksAsync(path)); // retried, not remembered as failed
        Assert.Equal(2, calls);
        File.Delete(path);
    }

    [Fact]
    public async Task The_cache_stays_bounded()
    {
        var service = new WaveformService(cacheCapacity: 3, decoder: (_, _) => Envelope);
        var paths = Enumerable.Range(0, 6).Select(_ => WriteTemp("x")).ToArray();

        foreach (var path in paths) await service.GetPeaksAsync(path);

        Assert.Equal(3, service.CachedCount);
        foreach (var path in paths) File.Delete(path);
    }

    [Fact]
    public async Task An_empty_path_decodes_to_nothing()
    {
        var service = new WaveformService(decoder: (_, _) => Envelope);
        Assert.Null(await service.GetPeaksAsync(null));
        Assert.Null(await service.GetPeaksAsync("  "));
    }
}

public class WaveformStalenessTests
{
    private static string WriteTemp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"b1-stale-{Guid.NewGuid():N}.wav");
        File.WriteAllText(path, "audio");
        return path;
    }

    [Fact]
    public async Task A_slow_decode_of_a_replaced_file_never_overwrites_the_new_waveform()
    {
        // The defect this covers: Replace file… started a fresh decode while the previous one was
        // still running; whichever finished last won, so the clip could end up showing the
        // envelope of a file it no longer references.
        var oldPath = WriteTemp();
        var newPath = WriteTemp();
        var release = new SemaphoreSlim(0);
        var slowDecoder = new DelegateWaveformDecoder(async path =>
        {
            if (path == oldPath) await release.WaitAsync();
            return path == oldPath ? new[] { 1f } : new[] { 2f };
        });

        var protocol = new FakeSequencerProtocol();
        using var viewModel = new SequencerViewModel(
            protocol, new FakeSequencerSettings(),
            library: new FakeSequenceLibraryService(),
            persistenceDialogs: new FakeSequencerPersistenceDialogs(),
            waveformDecoder: slowDecoder);

        var lane = viewModel.AudioLanes.First();
        var clip = new AudioClip { FilePath = oldPath, DurationMs = 1000 };
        Assert.True(viewModel.InsertAudioClip(lane, clip));

        var stale = viewModel.LoadWaveformAsync(clip);          // starts on the old file, blocks
        viewModel.ReplaceAudioClipSource(clip, newPath, 2000);  // bumps the token
        await viewModel.LoadWaveformAsync(clip);                // completes on the new file
        Assert.Equal(new[] { 2f }, clip.Peaks);

        release.Release();
        await stale;                                            // the old decode lands last…

        Assert.Equal(new[] { 2f }, clip.Peaks);                 // …and is discarded
        File.Delete(oldPath);
        File.Delete(newPath);
    }

    [Fact]
    public void Replacing_a_clip_source_clears_the_stale_envelope_and_bumps_the_token()
    {
        var protocol = new FakeSequencerProtocol();
        using var viewModel = new SequencerViewModel(
            protocol, new FakeSequencerSettings(),
            library: new FakeSequenceLibraryService(),
            persistenceDialogs: new FakeSequencerPersistenceDialogs());
        var lane = viewModel.AudioLanes.First();
        var clip = new AudioClip { FilePath = "old.wav", DurationMs = 1000, Peaks = new[] { 9f } };
        viewModel.InsertAudioClip(lane, clip);
        var before = clip.WaveformToken;

        viewModel.ReplaceAudioClipSource(clip, "new.wav", 2000);

        Assert.Null(clip.Peaks);
        Assert.NotEqual(before, clip.WaveformToken);
        Assert.Equal("new.wav", clip.FilePath);
        Assert.Equal("new.wav", clip.FileName); // SEQ-F03: derived name follows the path
    }

    [Fact]
    public void A_failed_probe_marks_the_clip_without_hiding_it()
    {
        var protocol = new FakeSequencerProtocol();
        using var viewModel = new SequencerViewModel(
            protocol, new FakeSequencerSettings(),
            library: new FakeSequenceLibraryService(),
            persistenceDialogs: new FakeSequencerPersistenceDialogs());
        var lane = viewModel.AudioLanes.First();
        var clip = new AudioClip { FilePath = "old.wav", DurationMs = 1000 };
        viewModel.InsertAudioClip(lane, clip);

        viewModel.ReplaceAudioClipSource(
            clip, "broken.mp3", 0,
            AudioProbeResult.Failure(AudioProbeStatus.DecodeFailed, "no codec"));

        Assert.True(clip.HasDurationWarning);
        Assert.False(clip.HasKnownDuration);
        Assert.Contains("no codec", clip.StatusTooltip);
        Assert.Equal(0, clip.DurationMs); // stays 0: it must not define the sequence end
    }

    [Fact]
    public void Loading_a_scene_flags_a_clip_whose_file_is_gone()
    {
        // A Scene stores paths, not audio. The operator should see the broken clip when the
        // Scene opens, not discover it when the pass reaches that timestamp.
        using var fixture = new TemporaryJsonFixture();
        var present = Path.Combine(fixture.DirectoryPath, "present.wav");
        File.WriteAllText(present, "audio");
        var missing = Path.Combine(fixture.DirectoryPath, "gone.wav");
        var json = $$"""
        {"type":"b1-sequence","version":5,"name":"Loaded","loop":false,"tracks":[],
         "audioLanes":[{"label":"VOICE","clips":[
           {"filePath":{{System.Text.Json.JsonSerializer.Serialize(present)}},"durationMs":100,"startMs":0,"loop":false},
           {"filePath":{{System.Text.Json.JsonSerializer.Serialize(missing)}},"durationMs":100,"startMs":200,"loop":false}]}],
         "steps":[]}
        """;

        using var viewModel = new SequencerViewModel(
            new FakeSequencerProtocol(), new FakeSequencerSettings(),
            library: new FakeSequenceLibraryService(),
            persistenceDialogs: new FakeSequencerPersistenceDialogs(),
            // Load fires waveform decoding per clip; a real decoder would still hold the file
            // open when this fixture's directory is deleted at teardown.
            waveformDecoder: new DelegateWaveformDecoder(_ => Task.FromResult<float[]?>(null)));
        viewModel.ImportFrom(fixture.Write("scene.b1seq.json", json));

        var clips = viewModel.AudioLanes.Single().Clips;
        Assert.False(clips[0].HasDurationWarning);
        Assert.True(clips[1].HasDurationWarning);
        Assert.Equal(AudioProbeStatus.FileMissing, clips[1].ProbeStatus);
        Assert.Contains("gone.wav", clips[1].StatusTooltip);
    }

    [Fact]
    public void A_playback_failure_surfaces_the_clip_name()
    {
        var protocol = new FakeSequencerProtocol();
        var player = new FakeAudioPlayer();
        using var viewModel = new SequencerViewModel(
            protocol, new FakeSequencerSettings(), player,
            library: new FakeSequenceLibraryService(),
            persistenceDialogs: new FakeSequencerPersistenceDialogs());

        Assert.False(viewModel.HasAudioFailures);
        player.RaiseFailure(2, @"C:\music\theme.mp3", "decoder unavailable");

        Assert.True(viewModel.HasAudioFailures);
        Assert.Contains("theme.mp3", viewModel.AudioFailureText);
        Assert.Contains("decoder unavailable", viewModel.AudioFailureText);

        player.RaiseFailure(2, @"C:\music\theme.mp3", "decoder unavailable");
        Assert.Single(viewModel.AudioFailureText.Split(Environment.NewLine));
    }
}

/// <summary>
/// The one test that touches a real decoder: NAudio decoding the committed MP3 fixture. It is the
/// automated half of SEQ-H05's "Media Foundation smoke test" — it fails on a Windows N/KN machine
/// with no MP3 codec, which is exactly the environment the installer warns about. The MediaPlayer
/// half needs a dispatcher and stays in tools/self-test.ps1.
/// </summary>
public class AudioCodecSmokeTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "probe-tone-1500ms.mp3");

    [Fact]
    public void The_fixture_is_present()
    {
        Assert.True(File.Exists(FixturePath), $"Missing audio fixture: {FixturePath}");
    }

    [Fact]
    public async Task A_real_mp3_decodes_into_a_rising_envelope()
    {
        var service = new WaveformService();

        var peaks = await service.GetPeaksAsync(FixturePath);

        Assert.NotNull(peaks);
        Assert.Equal(WaveformService.Resolution, peaks!.Length);
        Assert.All(peaks, peak => Assert.InRange(peak, 0f, 1f));

        // The fixture is a 440 Hz tone that fades in across its whole length, so the envelope has
        // to grow: a flat or empty result means the bucket mapping is wrong, not merely that some
        // audio decoded. Thirds rather than first/last sample, because an MP3 carries encoder
        // padding — the final buckets are legitimately silent.
        var third = peaks.Length / 3;
        var opening = peaks.Take(third).Max();
        var closing = peaks.Skip(2 * third).Max();
        Assert.True(closing > opening + 0.25f,
            $"expected a rising envelope, got opening={opening:F3} closing={closing:F3}");
        Assert.True(closing > 0.5f, $"expected an audible tone, peaked at {closing:F3}");
    }
}

// --- fakes -------------------------------------------------------------------

internal sealed class FakeMediaHandle : IMediaHandle
{
    private readonly Action<FakeMediaHandle> _onOpen;
    private readonly Exception? _openThrows;

    public FakeMediaHandle(Action<FakeMediaHandle> onOpen, Exception? openThrows)
    {
        _onOpen = onOpen;
        _openThrows = openThrows;
    }

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    public int? NaturalDurationMs { get; set; }
    public string? OpenedPath { get; private set; }
    public bool Disposed { get; private set; }
    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }
    public int StopCount { get; private set; }
    public int RewindCount { get; private set; }

    public void Open(string path)
    {
        OpenedPath = path;
        if (_openThrows != null) throw _openThrows;
        _onOpen(this);
    }

    public void Play() => PlayCount++;
    public void Pause() => PauseCount++;
    public void Stop() => StopCount++;
    public void Rewind() => RewindCount++;

    public void RaiseOpened() => Opened?.Invoke();
    public void RaiseEnded() => Ended?.Invoke();
    public void RaiseFailed(string message) => Failed?.Invoke(message);

    public void Dispose()
    {
        Disposed = true;
        Opened = null;
        Ended = null;
        Failed = null;
    }
}

internal sealed class FakeMediaHandleFactory : IMediaHandleFactory
{
    private readonly Action<FakeMediaHandle> _onOpen;

    public FakeMediaHandleFactory(Action<FakeMediaHandle> onOpen) => _onOpen = onOpen;

    public List<FakeMediaHandle> Created { get; } = new();
    public Exception? OpenThrows { get; set; }

    public IMediaHandle Create()
    {
        var handle = new FakeMediaHandle(_onOpen, OpenThrows);
        Created.Add(handle);
        return handle;
    }
}

internal sealed class DelegateWaveformDecoder : IWaveformDecoder
{
    private readonly Func<string, Task<float[]?>> _decode;

    public DelegateWaveformDecoder(Func<string, Task<float[]?>> decode) => _decode = decode;

    public Task<float[]?> GetPeaksAsync(string? path, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(path) ? Task.FromResult<float[]?>(null) : _decode(path);
}
