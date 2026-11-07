using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OllamaDesktop.Core;
namespace OllamaDesktop.App.Infrastructure;

internal static class WindowsImageDecoder
{
    // Called only by the disposable STA import worker. WMP is JPEG XR, not video.
    public static byte[] Decode(string file)
    {
        using var input=File.OpenRead(file);
        var decoder=new WmpBitmapDecoder(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.None);
        if(decoder.Frames.Count==0)throw new AppException("IMAGE_EMPTY","В JPEG XR нет изображения.");
        var frame=decoder.Frames[0];
        if((ulong)frame.PixelWidth*(ulong)frame.PixelHeight>Limits.ImagePixels)throw new AppException("IMAGE_PIXELS","Изображение превышает 40 мегапикселей.");
        var normalized=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(normalized));
        using var output=new MemoryStream();encoder.Save(output);
        return output.ToArray();
    }
}
