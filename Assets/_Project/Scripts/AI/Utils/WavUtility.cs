using System;
using System.IO;
using UnityEngine;

public static class WavUtility
{
    public static byte[] FromAudioClip(AudioClip clip)
    {
        if (clip == null)
        {
            Debug.LogError("[WavUtility] AudioClip is null in FromAudioClip.");
            return new byte[0];
        }
        if (clip.samples <= 0 || clip.channels <= 0)
        {
            Debug.LogError("[WavUtility] AudioClip has invalid samples or channels.");
            return new byte[0];
        }
        MemoryStream stream = new MemoryStream();
        int sampleCount = clip.samples * clip.channels;
        float[] samples = new float[sampleCount];
        try
        {
            clip.GetData(samples, 0);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[WavUtility] Failed to get AudioClip data: {ex.Message}");
            return new byte[0];
        }
        byte[] bytesData = ConvertAudioClipDataToInt16ByteArray(samples);

        stream.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"), 0, 4);
        stream.Write(BitConverter.GetBytes(36 + bytesData.Length), 0, 4);
        stream.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"), 0, 4);
        stream.Write(System.Text.Encoding.ASCII.GetBytes("fmt "), 0, 4);
        stream.Write(BitConverter.GetBytes(16), 0, 4);
        stream.Write(BitConverter.GetBytes((ushort)1), 0, 2);
        stream.Write(BitConverter.GetBytes((ushort)clip.channels), 0, 2);
        stream.Write(BitConverter.GetBytes(clip.frequency), 0, 4);

        int byteRate = clip.frequency * clip.channels * 2;
        stream.Write(BitConverter.GetBytes(byteRate), 0, 4);
        ushort blockAlign = (ushort)(clip.channels * 2);
        stream.Write(BitConverter.GetBytes(blockAlign), 0, 2);
        stream.Write(BitConverter.GetBytes((ushort)16), 0, 2);
        stream.Write(System.Text.Encoding.ASCII.GetBytes("data"), 0, 4);
        stream.Write(BitConverter.GetBytes(bytesData.Length), 0, 4);
        stream.Write(bytesData, 0, bytesData.Length);

        return stream.ToArray();
    }

    public static AudioClip ToAudioClip(byte[] wavData)
    {
        if (wavData == null || wavData.Length < 44)
        {
            Debug.LogError("[WavUtility] WAV data is null or too short.");
            return null;
        }

        try
        {
            using (var stream = new MemoryStream(wavData))
            using (var reader = new BinaryReader(stream))
            {
                string riff = ReadFourCC(reader);
                uint fileSize = reader.ReadUInt32();
                string wave = ReadFourCC(reader);

                if (riff != "RIFF" || wave != "WAVE")
                {
                    Debug.LogError("[WavUtility] Invalid WAV header.");
                    return null;
                }

                ushort audioFormat = 0;
                ushort channels = 0;
                int sampleRate = 0;
                ushort bitsPerSample = 0;
                byte[] dataChunk = null;

                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    string chunkId = ReadFourCC(reader);
                    uint chunkSize = reader.ReadUInt32();
                    long chunkSizeLong = chunkSize;

                    if (chunkId != "data" && reader.BaseStream.Position + chunkSizeLong > reader.BaseStream.Length)
                    {
                        Debug.LogError($"[WavUtility] Invalid WAV chunk size for chunk '{chunkId}' ({chunkSizeLong}).");
                        return null;
                    }

                    long chunkDataStart = reader.BaseStream.Position;

                    if (chunkId == "fmt ")
                    {
                        audioFormat = reader.ReadUInt16();
                        channels = reader.ReadUInt16();
                        sampleRate = reader.ReadInt32();
                        int byteRate = reader.ReadInt32();
                        ushort blockAlign = reader.ReadUInt16();
                        bitsPerSample = reader.ReadUInt16();

                        reader.BaseStream.Position = chunkDataStart + chunkSize;
                    }
                    else if (chunkId == "data")
                    {
                        long bytesRemaining = reader.BaseStream.Length - reader.BaseStream.Position;
                        int dataSize = chunkSize == uint.MaxValue || chunkSizeLong > bytesRemaining
                            ? (int)bytesRemaining
                            : (int)chunkSizeLong;
                        dataChunk = reader.ReadBytes(dataSize);
                        break;
                    }
                    else
                    {
                        reader.BaseStream.Position = chunkDataStart + chunkSizeLong;
                    }

                    if ((chunkSizeLong & 1) == 1 && reader.BaseStream.Position < reader.BaseStream.Length)
                    {
                        reader.BaseStream.Position++;
                    }
                }

                if (audioFormat != 1 || channels == 0 || sampleRate <= 0 || bitsPerSample != 16 || dataChunk == null || dataChunk.Length == 0)
                {
                    Debug.LogError("[WavUtility] Unsupported WAV format. Expected PCM 16-bit audio with data chunk.");
                    return null;
                }

                int sampleCount = dataChunk.Length / 2;
                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(dataChunk, i * 2);
                    samples[i] = sample / 32768f;
                }

                int frames = sampleCount / channels;
                AudioClip clip = AudioClip.Create("wav_clip", frames, channels, sampleRate, false);
                clip.SetData(samples, 0);
                return clip;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WavUtility] Failed to decode WAV: {ex.Message}");
            return null;
        }
    }

    private static string ReadFourCC(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
            throw new EndOfStreamException("Unexpected end of WAV data while reading chunk id.");

        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private static byte[] ConvertAudioClipDataToInt16ByteArray(float[] data)
    {
        MemoryStream dataStream = new MemoryStream();
        if (data == null || data.Length == 0)
            return dataStream.ToArray();
        foreach (var sample in data)
        {
            // Clamp sample to [-1, 1] to avoid overflow
            float clamped = Mathf.Clamp(sample, -1f, 1f);
            short intData = (short)(clamped * short.MaxValue);
            byte[] byteArr = BitConverter.GetBytes(intData);
            dataStream.Write(byteArr, 0, byteArr.Length);
        }
        return dataStream.ToArray();
    }
}
