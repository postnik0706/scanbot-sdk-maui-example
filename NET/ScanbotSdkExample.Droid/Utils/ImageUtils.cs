using Android.Content;
using Android.Graphics;

namespace ScanbotSdkExample.Droid.Utils;

public static class ImageUtils
{
    public static Bitmap ProcessGalleryResult(Context context, Intent data)
    {
        try
        {
            var uri = data?.Data;
            if (uri == null) return null;

            using var stream = context?.ContentResolver?.OpenInputStream(uri);
            if (stream == null) return null;
            return BitmapFactory.DecodeStream(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static byte[] ConvertToByteArray(IList<Java.Lang.Byte> rawBytes)
    {
        int count = rawBytes.Count;
        byte[] byteArray = new byte[count];
        
        // Optimized: cache count and minimize virtual calls
        for (int i = 0; i < count; i++)
        {
            var javaByteObj = rawBytes[i];
            byteArray[i] = javaByteObj != null ? (byte)javaByteObj.ByteValue() : (byte)0;
        }
        return byteArray;
    }
}