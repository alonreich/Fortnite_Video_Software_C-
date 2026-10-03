// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    /// <summary>
    /// AUD-MASTERVOL — re-applies the suite master to the Merger's players after ANY master change.
    /// Two fixes over the old copy: the gameplay player now carries the Music Wizard's VIDEO fader
    /// (it ignored it, so the preview balance was wrong), and the music player is updated live (it
    /// only picked the master up when its next track started).
    /// </summary>
    private void ApplyPreviewPlayersVolume()
    {
        _ = _videoHost?.IpcClient?.ApplyPreviewGainAsync(_musicResult?.VideoVolume ?? 1.0);
        if (_mergerMusicClient != null && _musicResult != null)
            _ = _mergerMusicClient.ApplyPreviewGainAsync(_musicResult.MusicVolume);
    }
}
