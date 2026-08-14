using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IBankLinkService
    {
        // Starts a Finverse Link session for this user; returns the URL
        // the frontend opens to run the Link UI.
        Task<string> StartLinkAsync(Guid userId, string redirectUri);

        // Exchanges the code Finverse's redirect handed back for an
        // access token, stores it, and performs the initial account +
        // transaction sync for that institution.
        Task<DashboardViewModel> CompleteLinkAsync(Guid userId, string linkCode);

        // Re-syncs every institution this user has already linked.
        // A failure on one institution does not stop the others from
        // syncing.
        Task<DashboardViewModel> SyncAsync(Guid userId);

        // Removes every linked institution this user has, along with the
        // accounts/transactions synced from them, so they can go through
        // the Connect flow again from a clean slate. Does not call
        // Finverse's own unlink API — this only clears this app's copy of
        // the connection.
        Task DisconnectAllAsync(Guid userId);
    }
}
