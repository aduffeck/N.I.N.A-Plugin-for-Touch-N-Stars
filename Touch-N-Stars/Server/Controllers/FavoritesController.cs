using TouchNStars.Server.Models;
using TouchNStars.Utility;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for favorite targets management
/// </summary>
public class FavoritesController : WebApiController
{
    private static readonly string FavoritesFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA", "TnsCache", "favorites.json"
    );

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Guards every read-modify-write of the file.
    private static readonly object _fileLock = new();

    // Callers hold _fileLock.
    private static List<FavoriteTarget> LoadLocked()
    {
        return JsonFileStore.Read(
            FavoritesFilePath,
            json => JsonSerializer.Deserialize<List<FavoriteTarget>>(json) ?? new List<FavoriteTarget>(),
            () => new List<FavoriteTarget>(),
            "favorites");
    }

    // Callers hold _fileLock.
    private static void SaveLocked(List<FavoriteTarget> favorites)
    {
        JsonFileStore.Write(FavoritesFilePath, JsonSerializer.Serialize(favorites, WriteOptions));
    }

    /// <summary>
    /// POST /api/favorites - Add a new favorite target
    /// </summary>
    [Route(HttpVerbs.Post, "/favorites")]
    public async Task<ApiResponse> AddFavoriteTarget()
    {
        try
        {
            var favorite = await HttpContext.GetRequestDataAsync<FavoriteTarget>();

            if (favorite.Id == Guid.Empty)
                favorite.Id = Guid.NewGuid();

            lock (_fileLock)
            {
                var currentFavorites = LoadLocked();
                currentFavorites.Add(favorite);
                SaveLocked(currentFavorites);
            }

            return new ApiResponse
            {
                Success = true,
                Response = favorite,
                StatusCode = 200,
                Type = "FavoriteTarget"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/favorites - Get all favorite targets
    /// </summary>
    [Route(HttpVerbs.Get, "/favorites")]
    public Task<List<FavoriteTarget>> GetFavoriteTargets()
    {
        try
        {
            lock (_fileLock)
            {
                return Task.FromResult(LoadLocked());
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return Task.FromResult(new List<FavoriteTarget>());
        }
    }

    /// <summary>
    /// DELETE /api/favorites/{id} - Delete a favorite target
    /// </summary>
    [Route(HttpVerbs.Delete, "/favorites/{id}")]
    public Task<ApiResponse> DeleteFavoriteTarget(Guid id)
    {
        try
        {
            lock (_fileLock)
            {
                var favorites = LoadLocked();

                if (!File.Exists(FavoritesFilePath))
                {
                    return Task.FromResult(new ApiResponse { Success = false, Error = "Keine Daten vorhanden", StatusCode = 404 });
                }

                var toRemove = favorites.FirstOrDefault(f => f.Id == id);
                if (toRemove == null)
                {
                    return Task.FromResult(new ApiResponse { Success = false, Error = "Eintrag nicht gefunden", StatusCode = 404 });
                }

                favorites.Remove(toRemove);
                SaveLocked(favorites);
            }

            return Task.FromResult(new ApiResponse { Success = true, Response = $"Favorit mit Id '{id}' gelöscht", StatusCode = 200 });
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return Task.FromResult(new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500 });
        }
    }

    /// <summary>
    /// PUT /api/favorites/{id} - Update a favorite target
    /// </summary>
    [Route(HttpVerbs.Put, "/favorites/{id}")]
    public async Task<ApiResponse> UpdateFavoriteTarget(Guid id)
    {
        try
        {
            var updatedTarget = await HttpContext.GetRequestDataAsync<FavoriteTarget>();

            FavoriteTarget existing;
            lock (_fileLock)
            {
                var favorites = LoadLocked();

                if (!File.Exists(FavoritesFilePath))
                {
                    return new ApiResponse { Success = false, Error = "Keine Daten vorhanden", StatusCode = 404 };
                }

                existing = favorites.FirstOrDefault(f => f.Id == id);
                if (existing == null)
                {
                    return new ApiResponse { Success = false, Error = "Eintrag nicht gefunden", StatusCode = 404 };
                }

                existing.Name = updatedTarget.Name;
                existing.Ra = updatedTarget.Ra;
                existing.Dec = updatedTarget.Dec;
                existing.DecString = updatedTarget.DecString;
                existing.RaString = updatedTarget.RaString;
                existing.Rotation = updatedTarget.Rotation;

                SaveLocked(favorites);
            }

            return new ApiResponse { Success = true, Response = existing, StatusCode = 200 };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500 };
        }
    }
}
