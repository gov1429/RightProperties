using System.Text.Json;
using System.Diagnostics;
using Windows.Storage;

namespace RightProperties;

// ffprobe uses stderr for logging.
// https://ffmpeg.org/ffprobe.html#:~:text=By%20default%20the%20program%20logs%20to%20stderr

static class FFProbe {
    static private string binaryPath = "ffprobe";

    /// <summary>
    /// A named tuple array records the relation between Windows Property, <see link="https://learn.microsoft.com/en-us/windows/win32/properties/props-system-proplist-fulldetails">System.PropList.FullDetails</see>,
    /// and section entries of ffprobe.
    /// Where <see name="winProp"/> is canonical name of properties under `System.PropGroup.Video` and `System.PropGroup.Audio` groups,
    /// <see name="ffEntry.sectionName"/> is the section name and
    /// <see name="ffEntry.sectionEntryName"/> is the name of local section entry of <see name="ffEntry.sectionName"/>.
    /// </summary>
    /// <remarks>
    /// The order of elements aligns to properties of `System.PropList.FullDetails`.
    /// </remarks>
    static private (string winProp, (string sectionName, string sectionEntryName) ffEntry)[] metaEntries = new[] {
        // https://github.com/FFmpeg/FFmpeg/blob/4aa1a42a91438b7107d2d77db1fc5ca95c27740c/doc/ffprobe.xsd
        // We should rely on: https://ffmpeg.org/doxygen/trunk/structAVFormatContext.html
        ("System.Media.Duration", ("format", "duration")), // Float -> Double.
        ("System.Video.FrameWidth", ("stream", "width")), // Int(Int32).
        ("System.Video.FrameHeight", ("stream", "height")), // Int(Int32).
        ("System.Video.EncodingBitrate", ("stream", "bit_rate")), // Int(Int32).
        ("System.Video.TotalBitrate", ("format", "bit_rate")), // Long(Int64).
        ("System.Video.FrameRate", ("stream", "avg_frame_rate")), // String.
        ("System.Audio.EncodingBitrate", ("stream", "bit_rate")), // Int(Int32).
        ("System.Audio.ChannelCount", ("stream", "channels")), // Int(Int32).
        ("System.Audio.SampleRate", ("stream", "sample_rate")) // Int(Int32).
    };

    // `codec_type` determines if a stream is video or audio.
    // `format_name` checks if the media has correct file extension or not.
    static private Dictionary<string, string[]> ffExtraEntries = new Dictionary<string, string[]> {
        { "stream", new[] { "codec_type" } },
        { "format", new[] { "format_name", "probe_score" } }
    };

    static private Dictionary<string, string> formatExtMap = new Dictionary<string, string> {
        // https://github.com/FFmpeg/FFmpeg/blob/b61733f61ff2f61b1208fb661e278f704680d6a6/libavformat/mpeg.c#L692
        // https://github.com/FFmpeg/FFmpeg/blob/40aa451154fc54b661036bfb90532199269397bc/libavformat/mpegenc.c#L1298
        { "mpeg", "mpg,mpeg" },
        // https://github.com/FFmpeg/FFmpeg/blob/b61733f61ff2f61b1208fb661e278f704680d6a6/libavformat/asfdec_f.c#L1620
        // https://github.com/FFmpeg/FFmpeg/blob/b61733f61ff2f61b1208fb661e278f704680d6a6/libavformat/asfenc.c#L1130
        { "asf", "asf,wmv,wma" },
        // https://web.archive.org/web/20120317061837/http://www.xiph.org:80/container/ogm.html
        // https://github.com/FFmpeg/FFmpeg/blob/b61733f61ff2f61b1208fb661e278f704680d6a6/libavformat/oggdec.c#L963
        // https://github.com/FFmpeg/FFmpeg/blob/b61733f61ff2f61b1208fb661e278f704680d6a6/libavformat/oggenc.c#L750
        { "ogg", "ogg,ogv,spx,opus" },
        // https://github.com/FFmpeg/FFmpeg/blob/b61733f61ff2f61b1208fb661e278f704680d6a6/libavformat/matroskadec.c#L4790
        { "matroska,webm", "mkv,mk3d,mka,mks,webm" },
        // https://github.com/FFmpeg/FFmpeg/blob/b61733f61ff2f61b1208fb661e278f704680d6a6/libavformat/mov.c#L9304
        { "mov,mp4,m4a,3gp,3g2,mj2", "mov,mp4,m4a,3gp,3g2,mj2,psp,m4b,ism,ismv,isma,f4v,avif" }
    };

    static private Process ExecuteFFProbeCommand(string ffprobeArg, DataReceivedEventHandler? handler = null) {
        Const.CTS.Token.ThrowIfCancellationRequested();
        string error = "";
        var process = new Process();
        process.StartInfo.FileName = binaryPath;
        process.StartInfo.Arguments = ffprobeArg;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (_, e) => { error += $"  {e.Data}\r\n"; };
        process.Exited += (_, _) => {
            if (error.Trim().Length > 0) Logger.Warn("ffprobe stderr from `{0}`:\r\n{1}", ffprobeArg[(ffprobeArg.LastIndexOf('"', ffprobeArg.Length - 2) + 1)..^1], error[..^6]);
        };

        process.Start();
        process.BeginErrorReadLine();
        if (handler != null) {
            process.OutputDataReceived += handler;
            process.BeginOutputReadLine();
        }

        Const.CTS.Token.Register(() => {
            try {
                if (process.HasExited) return;
                process.Kill();
                process.Dispose();
            } catch (InvalidOperationException e) {
                Logger.Silent("", e);
            } catch (Exception e) {
                Logger.Error("Kill ffprobe process failed:\r\n{0}", e);
            }
        });

        return process;
    }

    /// <summary>
    /// Pre-fetch duration of stream.
    /// This method determines if <see cref="CalculateStreamBitrate(string, List{string}, Dictionary{string, object})"/> should accumulate duration of stream or not.
    /// </summary>
    /// <remarks>
    /// ffprobe may not fetch duration of stream("N/A"). It seems this strategy doesn't gain performance very much.
    /// </remarks>
    static private async Task<(List<string> timeBases, List<string> durations)> FetchStreamDuration(string filePath, List<string> streamTypes) {
        string ffprobeArg = $"-show_entries stream=time_base,duration -hide_banner -loglevel warning -print_format csv=print_section=0 \"{filePath}\"";
        if (streamTypes.Count == 1) ffprobeArg = $"-select_streams {streamTypes[0][0]} {ffprobeArg}";

        var streamTimeBases = new List<string>();
        var streamDurations = new List<string>(); // We need to handle N/A case.

        using Process process = ExecuteFFProbeCommand(ffprobeArg, (_, e) => {
            if (e.Data == null) return; // End.

            string[] cols = e.Data.Split(',');
            streamTimeBases.Add(cols[0]);
            streamDurations.Add(cols[1]);
        });

        await process.WaitForExitAsync(Const.CTS.Token).ConfigureAwait(false);

        return (streamTimeBases, streamDurations);

        // (List<string> streamTimeBases, List<string> streamDurations) = await FetchStreamDuration(filePath, streamTypes);
        // List<bool> shouldCalcDurByIdx = streamDurations.ConvertAll(d => d == "N/A");
        // bool shouldCalcDur = shouldCalcDurByIdx.Contains(true);

        // bool isTwoStreams = streamTypes.Count == 2;
        // (int durationIdx, int sizeIdx) = isTwoStreams ? (1, 2) : (0, 1);
        // if (!shouldCalcDur) sizeIdx--;

        // var streamSumSizes = new long[streamTypes.Count];
        // var streamSumDurations = new long[streamTypes.Count];
        // Array.Fill(streamSumSizes, 0L);
        // Array.Fill(streamSumDurations, 0L);

        // // -read_intervals xxxx(seconds)
        // // We may can't get duration from stream, so we need to sum it by ourself.
        // string ffprobeArg = $"-show_entries packet={(isTwoStreams ? "codec_type," : "")}{(shouldCalcDur ? "duration" : "")},size -hide_banner -loglevel warning -print_format csv=print_section=0 \"{filePath}\"";
        // if (!isTwoStreams) ffprobeArg = $"-select_streams {streamTypes[0][0]} {ffprobeArg}";

        // using Process process = ExecuteFFProbeCommand(ffprobeArg, (_, e) => {
        //     if (e.Data == null) return; // End.

        //     string[] cols = e.Data.Split(',');
        //     int streamIdx = cols[0] == "audio" ? 1 : 0;
        //     if (Int32.TryParse(cols[sizeIdx], out var size)) streamSumSizes[streamIdx] += size;
        //     if (shouldCalcDurByIdx[streamIdx] && Int32.TryParse(cols[durationIdx], out var duration)) streamSumDurations[streamIdx] += duration;
        // });
    }

    static private async Task CalculateStreamBitrate(string filePath, List<string> streamTypes, Dictionary<string, object> winProps) {
        // The reason why we have this function is because ffprobe can't retrieve the notional bitrate(set for the encoder).
        // Note that the calculated bitrate is approximation, to get actual bitrate, use ffmpeg or mediainfo to parse it.
        // https://stackoverflow.com/a/54609719
        Logger.Info("Try to calculate bitrate of stream(s), {0}, from `{1}`.", String.Join(" and ", streamTypes), filePath);

        bool isTwoStreams = streamTypes.Count == 2;
        (int durationIdx, int sizeIdx) = isTwoStreams ? (2, 3) : (1, 2);
        var streamSumSizes = new long[streamTypes.Count];
        var streamSumDurations = new long[streamTypes.Count];
        var streamTimeBases = new List<string>();
        var streamDurations = new List<string>(); // We need to handle N/A case.
        Array.Fill(streamSumSizes, 0L);
        Array.Fill(streamSumDurations, 0L);

        // -read_intervals xxxx(seconds)
        // We may can't get duration from stream, so we need to sum it by ourself.
        string ffprobeArg = $"-show_entries packet={(isTwoStreams ? "codec_type," : "")}duration,size:stream=time_base,duration -hide_banner -loglevel warning -print_format csv \"{filePath}\"";
        if (!isTwoStreams) ffprobeArg = $"-select_streams {streamTypes[0][0]} {ffprobeArg}";

        using Process process = ExecuteFFProbeCommand(ffprobeArg, (_, e) => {
            if (e.Data == null) return; // End.

            string[] cols = e.Data.Split(',');
            if (cols[0] == "packet") {
                int streamIdx = cols[1] == "audio" ? 1 : 0;
                if (Int32.TryParse(cols[sizeIdx], out var size)) streamSumSizes[streamIdx] += size;
                if (Int32.TryParse(cols[durationIdx], out var duration)) streamSumDurations[streamIdx] += duration;
            } else if (cols[0] == "stream") {
                streamTimeBases.Add(cols[1]);
                streamDurations.Add(cols[2]);
            }
        });

        await process.WaitForExitAsync(Const.CTS.Token).ConfigureAwait(false);

        for (int idx = 0; idx < streamTypes.Count; idx++) {
            if (!Double.TryParse(streamDurations[idx], out var duration)) {
                Logger.Warn("ffprobe failed to parse duration of stream({0}) from `{1}`, got `{2}`.", streamTypes[idx], filePath, streamDurations[idx]);
                string[] rational = streamTimeBases[idx].Split('/');
                duration = streamSumDurations[idx] * Int32.Parse(rational[0]) / Int32.Parse(rational[1]);
            }

            // Unit of `videoStreamSize` is byte, multiply 8 to convert to bit.
            // Bitrate ~= size(bit) / duration.
            string bitPerSecond = Math.Round(8 * streamSumSizes[idx] / duration, 0, MidpointRounding.AwayFromZero).ToString();
            Logger.Debug("{0} stream info of `{1}`:\r\n  Size: {2} bytes\r\n  Duration: {3} seconds\r\n  Bitrate: {4} bit per second", streamTypes[idx], filePath, streamSumSizes[idx], duration, bitPerSecond);
            winProps.Add($"FFProbe.{Char.ToUpper(streamTypes[idx][0])}{streamTypes[idx].Substring(1)}.EncodingBitrate.Calculated", bitPerSecond);
        }
    }

    static public async Task CalculateFrameRate(string filePath, Dictionary<string, object> winProps) {
        Logger.Info("Try to calculate average frame rage of video from `{0}`.", filePath);
        // `-count_frames` is much slower than `-count_packets`.
        string ffprobeArg = $"-select_streams v -count_packets -show_entries stream=time_base,duration,nb_read_packets -hide_banner -loglevel warning -print_format csv=print_section=0 \"{filePath}\"";
        var probeCols = new string[3];
        using Process process = ExecuteFFProbeCommand(ffprobeArg, (_, e) => {
            if (e.Data == null) return;
            probeCols = e.Data.Split(',');
        });

        await process.WaitForExitAsync(Const.CTS.Token).ConfigureAwait(false);

        if (!Double.TryParse(probeCols[1], out var duration)) {
            Logger.Warn("ffprobe failed to parse duration of video stream from `{0}`, got `{1}`.", filePath, probeCols[1]);
            ffprobeArg = $"-select_streams v -show_entries packet=duration -hide_banner -loglevel warning -print_format default=nokey=1:noprint_wrappers=1 \"{filePath}\"";
            int sumDuration = 0;
            using Process durationProcess = ExecuteFFProbeCommand(ffprobeArg, (_, e) => {
                if (e.Data == null) return;
                if (Int32.TryParse(e.Data, out var duration)) sumDuration += duration;
            });

            string[] rational = probeCols[0].Split('/');
            duration = sumDuration * Int32.Parse(rational[0]) / Int32.Parse(rational[1]);
        }

        winProps.Add($"FFProbe.Video.FrameRate.Calculated", $"{probeCols[2]}/{duration}");
    }

    static public void SetBinaryPath(string path) {
        binaryPath = path;
    }

    static public void CheckFFProbeBinary() {
        try {
            using Process process = ExecuteFFProbeCommand("-version");
            process.WaitForExit();
        } catch (Exception e) {
            Logger.Error("Unable to execute ffprobe, do you have installed it?\r\n{0}", e);
        }
    }

    /// <summary>
    /// Prepare options to be passed to ffrpobe based on <paramref name="winProps"/>.<br/>
    /// It compares to properties' name of <see cref="metaEntries"/> to lookup missing properties.
    /// </summary>
    /// <param name="winProps">
    /// The awaited result from <see cref="Windows.Storage.FileProperties.StorageItemContentProperties.RetrievePropertiesAsync(IEnumerable{string})"/>.
    /// </param>
    /// <returns>
    /// The 1st tuple item contains options to be passed to ffprobe.<br/>
    /// The 2nd tuple item collects indexes of <see cref="metaEntries"/> for later mapping purpose.
    /// </returns>
    /// <remarks>
    /// By checking <see cref="String.Length">Length</see> or <see cref="List{T}.Count">Count</see> of items from returned tuple
    /// to know whether there is any missing property or not.
    /// </remarks>
    static public (string entriesArg, List<int> queryIndexes) PrepareFFProbeEntriesArgument(IDictionary<string, object> winProps) {
        var entries = new Dictionary<string, HashSet<string>> { { "stream", new HashSet<string>() }, { "format", new HashSet<string>() } };
        var queryIndexes = new List<int>();
        string arg = "";

        for (int index = 0; index < metaEntries.Length; index++) {
            var meta = metaEntries[index];
            if (!winProps.ContainsKey(meta.winProp)) {
                entries[meta.ffEntry.sectionName].Add(meta.ffEntry.sectionEntryName);
                queryIndexes.Add(index);
            }
        }

        // Found everything we want, skip it.
        if (queryIndexes.Count == 0) return (arg, queryIndexes);

        foreach (var extraEntry in ffExtraEntries)
            if (entries[extraEntry.Key].Count > 0) {
                foreach (var sectionEntry in extraEntry.Value) entries[extraEntry.Key].Add(sectionEntry);
                arg += $"{extraEntry.Key}={String.Join(',', entries[extraEntry.Key])}:";
            } else arg += $"{extraEntry.Key}={String.Join(',', extraEntry.Value)}:";

        // TODO: Only retrieve artist(artist,album_artist,author) instead format_tags later.
        if (arg.Length > 0) arg += "error:format_tags";

        return (arg, queryIndexes);
    }

    /// <summary>
    /// Retrieve metadata(depends on <paramref name="entriesArg"/>) of given file.<br/>
    /// It adds missing properties from <see cref="metaEntries"/> and replaces the prefix, `System`, with `FFProbe`.
    /// Following properties may be found also if ffprobe is able to fetch it:
    /// <list type="number">
    ///     <item>FFProbe.Format.Tags</item>
    ///     <item>FFProbe.Music.Artist</item>
    ///     <item>
    ///         <term>FFProbe.Video/Audio.EncodingBitrate.Calculated</term>
    ///         <description>Calculate by our own cuz ffprobe can't retrieve the bitrate set by encoders.</description>
    ///     </item>
    /// </list>
    /// </summary>
    /// <param name="entriesArg">
    /// Usually comes from returned value(1st item of tuple) of <see cref="PrepareFFProbeEntriesArgument(IDictionary{string, object})"/>.
    /// </param>
    /// <param name="queryIndexes">
    /// Usually comes from returned value(2nd item of tuple) of <see cref="PrepareFFProbeEntriesArgument(IDictionary{string, object})"/>.
    /// </param>
    /// <param name="inWinProps">
    /// The awaited result from <see cref="Windows.Storage.FileProperties.StorageItemContentProperties.RetrievePropertiesAsync(IEnumerable{string})"/>.
    /// </param>
    /// <returns>
    /// A copied of <paramref name="inWinProps"/> with metadata retrieved from ffprobe.
    /// </returns>
    /// <remarks>
    /// Usually called immediately after <see cref="PrepareFFProbeEntriesArgument"/>.
    /// </remarks>
    static public async Task<Dictionary<string, object>> GetMetadataFromFFProbe(StorageFile file, string entriesArg, List<int> queryIndexes, IDictionary<string, object> inWinProps) {
        // TODO: Unable to `Add` IDictionary https://github.com/microsoft/CsWinRT/blob/0e9147967239e902eb7898fed2d4839fce2c7ec5/src/WinRT.Runtime/Projections/IDictionary.net5.cs#L208 
        var winProps = new Dictionary<string, object>(inWinProps);
        using Process process = ExecuteFFProbeCommand($"-show_entries {entriesArg} -hide_banner -loglevel warning -print_format json=compact=1 \"{file.Path}\"");

        var exitTask = process.WaitForExitAsync(Const.CTS.Token).ConfigureAwait(false);
        var docTask = JsonDocument.ParseAsync(process.StandardOutput.BaseStream, default, Const.CTS.Token).ConfigureAwait(false);
        await exitTask;
        using JsonDocument doc = await docTask;

        // TODO: Throw? -show_error; Logger.Error?
        if (doc.RootElement.TryGetProperty("error", out JsonElement err))
            Logger.Warn("ffprobe error section:\r\n  {0}", err);

        JsonElement sectionElm = doc.RootElement.GetProperty("streams");
        var anotherCodecIndexes = new List<int>();
        var formatIndexes = new List<int>();
        var noBitrateStreams = new List<string>();
        int streamCounter = 0;
        foreach (JsonElement stream in sectionElm.EnumerateArray()) {
            if (++streamCounter > 2) ThrowWithVideoMeta("#stream of a video > 2.");

            string? codecType = stream.GetProperty("codec_type").GetString();
            // Usually this won't happen. We expect there are only audio and video streams.
            if (codecType != "video" && codecType != "audio") ThrowWithVideoMeta("The codec is neither \"video\" nor \"audio\".");

            // This usually runs at second stream.
            if (anotherCodecIndexes.Count > 0) anotherCodecIndexes.ForEach(metaIdx => CollectMetadata(stream, metaIdx, codecType));
            else queryIndexes.ForEach(metaIdx => {
                // Traversal all indexes first and collect current codec's metadata,
                // then collect remaining indexes' meta in `anotherCodecIndexes` and `formatIndexes`.
                var metaEntry = metaEntries[metaIdx];
                if (metaEntry.ffEntry.sectionName != "stream") {
                    formatIndexes.Add(metaIdx);
                    return;
                }

                // We handle video kind first(usually the first stream).
                if ((codecType == "video") == (metaIdx < 6)) CollectMetadata(stream, metaIdx, codecType);
                else anotherCodecIndexes.Add(metaIdx);
            });
        }

        if (streamCounter != 2) ThrowWithVideoMeta("#stream of a video != 2.");

        var calculationTasks = new List<Task>();
        if (noBitrateStreams.Count > 0) calculationTasks.Add(CalculateStreamBitrate(file.Path, noBitrateStreams, winProps));

        // Rare case.
        if (winProps.TryGetValue("FFProbe.Video.FrameRate", out var framerate) && framerate.Equals("0/0")) {
            calculationTasks.Add(CalculateFrameRate(file.Path, winProps));
            winProps.Remove("FFProbe.Video.FrameRate");
        }

        sectionElm = doc.RootElement.GetProperty("format");
        // Verify overall. https://github.com/FFmpeg/FFmpeg/blob/b6066ceb8bd1e3ae1af733d22cb1b5c234c47a0b/libavformat/avformat.h#L1529
        // https://stackoverflow.com/a/25288882
        if (sectionElm.GetProperty("probe_score").GetInt32() != 100)
            Logger.Debug("`probe_score` of {0} is not 100.", file.Path);

        // All format: https://github.com/FFmpeg/FFmpeg/blob/b61733f61ff2f61b1208fb661e278f704680d6a6/libavformat/allformats.c
        // Verify extension. https://github.com/FFmpeg/FFmpeg/blob/9d70e74d255dbe37af52b0efffc0f93fd7cb6103/libavformat/avformat.h#L547-L551
        // Common extensions: https://github.com/FFmpeg/FFmpeg/blob/5acc3c4cff88de1ced1c4d9c6070b708613b43c1/fftools/opt_common.c#L431
        string formatName = sectionElm.GetProperty("format_name").GetString()!;
        string fileType = file.FileType.Substring(1);
        if (!formatName.Contains(fileType) && !(formatExtMap.TryGetValue(formatName, out var ext) && ext.Contains(fileType)))
            Logger.Warn("`{0}` isn't listed on ffprobe format `{1}`'s extensions. (File: {2})", file.FileType, sectionElm.GetProperty("format_name").GetString()!, file.Path);

        formatIndexes.ForEach(metaIdx => CollectMetadata(sectionElm, metaIdx, null));

        // Retrieve for artist meta.
        if (sectionElm.TryGetProperty("tags", out var tags)) {
            winProps.Add("FFProbe.Format.Tags", tags.Clone());
            // TODO: Remove this when we know all artist related fields.
            Logger.Debug("Tags of `{0}`:\r\n  {1}", file.Path, tags);

            foreach (string artistKey in new[] { "artist", "album_artist" })
                if (tags.TryGetProperty(artistKey, out JsonElement artist)) {
                    // TODO: The prop group are different between video and audio.
                    winProps.Add("FFProbe.Music.Artist", artist.GetString()!);
                    break;
                }
        }

        if (calculationTasks.Count > 0) await Task.WhenAll(calculationTasks).ConfigureAwait(false);

        return winProps;

        void ThrowWithVideoMeta(string message) {
            Logger.Debug("Video meta of {0}:\r\n{1}", file.Path, doc.RootElement);
            throw new Exception(message);
        }

        void CollectMetadata(JsonElement entry, int metaIndex, string? codecType) {
            var metaEntry = metaEntries[metaIndex];
            JsonElement streamProp;

            if (metaEntry.ffEntry.sectionEntryName == "bit_rate" && codecType != null) {
                // `entry` must be a stream here.
                if (!entry.TryGetProperty(metaEntry.ffEntry.sectionEntryName, out streamProp)) {
                    // Somehow ffprobe can't get the bitrate from the header/metadata, we calculate it.
                    noBitrateStreams.Add(codecType);
                    return;
                }
            } else streamProp = entry.GetProperty(metaEntry.ffEntry.sectionEntryName);

            winProps.Add("FFProbe" + metaEntry.winProp.Substring(6), streamProp.ValueKind == JsonValueKind.Number ? streamProp.GetInt32() : streamProp.GetString()!);
            // Wrap try-catch here to debug unknown error.
        }
    }
}