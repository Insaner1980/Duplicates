using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Duplicates.Services;

internal sealed class WindowsVideoTranscodeBackend : IVideoTranscodeBackend
{
    public async Task<VideoTranscodeBackendResult> TranscodeAsync(
        string sourcePath,
        string destinationPath,
        VideoTranscodeProfile profile,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            StorageFile source = await WinRtAsync.AwaitAndCloseAsync(
                    StorageFile.GetFileFromPathAsync(sourcePath),
                    cancellationToken)
                .ConfigureAwait(false);
            StorageFile destination = await WinRtAsync.AwaitAndCloseAsync(
                    StorageFile.GetFileFromPathAsync(destinationPath),
                    cancellationToken)
                .ConfigureAwait(false);
            VideoProperties sourceProperties = await WinRtAsync.AwaitAndCloseAsync(
                    source.Properties.GetVideoPropertiesAsync(),
                    cancellationToken)
                .ConfigureAwait(false);
            MediaEncodingProfile encodingProfile = CreateEncodingProfile(
                profile,
                sourceProperties.Orientation);
            var transcoder = new MediaTranscoder
            {
                AlwaysReencode = true,
                HardwareAccelerationEnabled = profile.HardwareAccelerationEnabled,
            };
            PrepareTranscodeResult preparation = await WinRtAsync.AwaitAndCloseAsync(
                    transcoder.PrepareFileTranscodeAsync(source, destination, encodingProfile),
                    cancellationToken)
                .ConfigureAwait(false);
            VideoTranscodeBackendResult preparationResult = MapPreparation(
                preparation.CanTranscode,
                preparation.FailureReason);
            if (preparationResult.Outcome != VideoTranscodeBackendOutcome.Succeeded)
            {
                return preparationResult;
            }

            await WinRtAsync.AwaitAndCloseAsync(
                    preparation.TranscodeAsync(),
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            return new VideoTranscodeBackendResult(
                VideoTranscodeBackendOutcome.Succeeded,
                "Windows MediaTranscoder completed the output.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MapRuntimeFailure(exception);
        }
    }

    internal static VideoTranscodeBackendResult MapRuntimeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new VideoTranscodeBackendResult(
            VideoTranscodeBackendOutcome.Failed,
            "Windows MediaTranscoder failed while creating the optimized output.");
    }

    internal static VideoEncodingQuality QualityFor(VideoOptimizationPreset preset) => preset switch
    {
        VideoOptimizationPreset.Smaller => VideoEncodingQuality.HD720p,
        VideoOptimizationPreset.Balanced => VideoEncodingQuality.HD1080p,
        VideoOptimizationPreset.HighQuality => VideoEncodingQuality.Uhd2160p,
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    internal static MediaEncodingProfile CreateEncodingProfile(
        VideoTranscodeProfile requested,
        VideoOrientation sourceOrientation)
    {
        ArgumentNullException.ThrowIfNull(requested);
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(QualityFor(requested.Preset));
        VideoEncodingProperties video = profile.Video ??
            throw new InvalidOperationException("The selected MP4 preset has no video profile.");
        (uint rawWidth, uint rawHeight) = sourceOrientation switch
        {
            VideoOrientation.Normal or VideoOrientation.Rotate180 =>
                (requested.Width, requested.Height),
            VideoOrientation.Rotate90 or VideoOrientation.Rotate270 =>
                (requested.Height, requested.Width),
            _ => throw new InvalidDataException("The source video orientation is invalid."),
        };
        video.Width = rawWidth;
        video.Height = rawHeight;
        video.Bitrate = requested.VideoBitrate;
        video.Subtype = MediaEncodingSubtypes.H264;
        video.FrameRate.Numerator = requested.FrameRateNumerator;
        video.FrameRate.Denominator = requested.FrameRateDenominator;
        video.PixelAspectRatio.Numerator = requested.PixelAspectRatioNumerator;
        video.PixelAspectRatio.Denominator = requested.PixelAspectRatioDenominator;

        if (!requested.IncludeAudio)
        {
            profile.Audio = null;
        }
        else
        {
            AudioEncodingProperties audio = profile.Audio ??
                throw new InvalidOperationException("The selected MP4 preset has no audio profile.");
            audio.Subtype = MediaEncodingSubtypes.Aac;
            audio.Bitrate = requested.AudioBitrate;
        }

        return profile;
    }

    internal static VideoTranscodeBackendResult MapPreparation(
        bool canTranscode,
        TranscodeFailureReason reason)
    {
        if (canTranscode)
        {
            return new VideoTranscodeBackendResult(
                VideoTranscodeBackendOutcome.Succeeded,
                "The Windows transcode operation is prepared.");
        }

        return reason switch
        {
            TranscodeFailureReason.CodecNotFound => new VideoTranscodeBackendResult(
                VideoTranscodeBackendOutcome.CodecNotFound,
                "A required Windows media codec was not found."),
            TranscodeFailureReason.InvalidProfile => new VideoTranscodeBackendResult(
                VideoTranscodeBackendOutcome.InvalidProfile,
                "Windows MediaTranscoder rejected the selected output profile."),
            TranscodeFailureReason.Unknown => new VideoTranscodeBackendResult(
                VideoTranscodeBackendOutcome.Failed,
                "Windows MediaTranscoder could not prepare this input."),
            _ => new VideoTranscodeBackendResult(
                VideoTranscodeBackendOutcome.Failed,
                "Windows MediaTranscoder reported that the input cannot be transcoded."),
        };
    }
}
