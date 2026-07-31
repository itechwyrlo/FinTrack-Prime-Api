using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    // Contract only. FinTrackPrime.WebApi depends on this interface,
    // never on AuthService directly, so the implementation can change
    // without touching the controller. Every method that establishes a
    // session returns an AuthResult (access token + raw refresh token),
    // never a bare AuthResponse — the raw refresh token only exists
    // transiently here, on its way to becoming an HttpOnly cookie in
    // the controller.
    public interface IAuthService
    {
        Task<AuthResult> RegisterAsync(RegisterRequest request);
        Task<AuthResult> LoginAsync(LoginRequest request);
        Task<AuthResult> LoginWithGoogleAsync(string googleIdToken);
        Task<AuthResult> RefreshAsync(string refreshToken);
        Task LogoutAsync(string refreshToken);
    }
}
