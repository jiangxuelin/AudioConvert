namespace AudioConvert.Services
{
    public enum AudioOutputFormat
    {
        Mp3,
        Wav,
        Flac,
        Ogg
    }

    public static class AudioOutputFormatExtensions
    {
        public static string GetExtension(this AudioOutputFormat format) =>
            format switch
            {
                AudioOutputFormat.Mp3 => ".mp3",
                AudioOutputFormat.Wav => ".wav",
                AudioOutputFormat.Flac => ".flac",
                AudioOutputFormat.Ogg => ".ogg",
                _ => ".mp3"
            };

        public static string GetDisplayName(this AudioOutputFormat format) =>
            format switch
            {
                AudioOutputFormat.Mp3 => "MP3",
                AudioOutputFormat.Wav => "WAV",
                AudioOutputFormat.Flac => "FLAC",
                AudioOutputFormat.Ogg => "OGG",
                _ => "MP3"
            };
    }
}
