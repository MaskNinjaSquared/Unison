using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Turns a picked image into what <c>w:profile:picture</c> accepts: a square JPEG, centre
    /// cropped and scaled (Baileys <c>generateProfilePicture</c>, 640×640).
    /// </summary>
    public interface IProfilePictureEncoder
    {
        /// <returns>The JPEG bytes, or null when the image cannot be decoded.</returns>
        Task<byte[]> EncodeSquareJpegAsync(byte[] image);
    }
}
