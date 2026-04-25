using FFMpegCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FenixProFmodAva.ViewModels
{
    public class AudioConverter
    {
        public static async Task ConvertToWavAsync(string inputPath, string outputWavPath)
        {
            if (!File.Exists(inputPath))
            {
                return;
            }

            try
            {
                // FFMpegArguments 会从输入和输出文件的扩展名自动推断出需要进行的转换
                await FFMpegArguments
                    .FromFileInput(inputPath)
                    .OutputToFile(outputWavPath, true, options => options
                        // 您可以在此处添加额外的 FFmpeg 参数，例如设置音频编解码器
                        // 对于 WAV，通常使用 pcm_s16le 编码
                        .WithAudioCodec("pcm_s16le"))
                    .ProcessAsynchronously();

                Console.WriteLine($"文件转换成功！已将 '{inputPath}' 转换为 '{outputWavPath}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"文件转换过程中发生错误：{ex.Message}");
            }
        }
    }
}
