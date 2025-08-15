using System.Net.Mime;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EditorsChoicePlugin.Configuration;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EditorsChoicePlugin.Api;

[ApiController]
[Route("editorschoice")]
public class EditorsChoiceActivityController : ControllerBase
{
    private readonly ILogger<EditorsChoiceActivityController> _logger;
    private readonly PluginConfiguration _config;
    private readonly string _scriptPath;

    public EditorsChoiceActivityController(ILogger<EditorsChoiceActivityController> logger)
    {
        _logger = logger;
        _config = Plugin.Instance!.Configuration;
        _scriptPath = GetType().Namespace + ".client.js";
        _logger.LogInformation("EditorsChoiceActivityController loaded.");
    }

    [HttpGet("script")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(_scriptPath);
        return stream is not null ? File(stream, "application/javascript") : NotFound();
    }

    [HttpGet("favourites")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<Dictionary<string, object>> GetFavourites(
        [FromServices] IUserManager userManager,
        [FromServices] ILibraryManager libraryManager)
    {
        try
        {
            Dictionary<string, object> response;
            List<object> items;
            InternalItemsQuery query;
            List<BaseItem> initialResult = [];
            List<BaseItem> result = [];
            bool resultsEmpty = false;
            int? maximumParentRating = null;
            bool? mustHaveParentRating = null;

            // Active user
            var name = User?.Identity?.Name ?? string.Empty;
            var activeUser = userManager.GetUserByName(name);
            if (activeUser is null) return NotFound();

            // Parental rating logic
            if (_config.MaximumParentRating == -2)
            {
                maximumParentRating = activeUser.MaxParentalAgeRating;
                if (maximumParentRating >= 0) mustHaveParentRating = true;
            }
            else
            {
                maximumParentRating = _config.MaximumParentRating;
                mustHaveParentRating = true;
            }

            // FAVOURITES mode
            if (_config.Mode == "FAVOURITES")
            {
                if (string.IsNullOrWhiteSpace(_config.EditorUserId) || _config.EditorUserId.Length < 16)
                {
                    resultsEmpty = true;
                }
                else
                {
                    var editorUser = userManager.GetUserById(Guid.Parse(_config.EditorUserId));
                    query = new InternalItemsQuery(editorUser)
                    {
                        IsFavorite = true,
                        IncludeItemsByName = true,
                        IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Season],
                        MinCommunityRating = _config.MinimumRating,
                        MinCriticRating = _config.MinimumCriticRating,
                        MaxParentalRating = maximumParentRating,
                        HasParentalRating = mustHaveParentRating,
                        OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                        Limit = _config.RandomMediaCount * 2
                    };
                    initialResult = libraryManager.GetItemList(query);

                    // IDs visible to active user
                    var itemIds = new HashSet<Guid>(
                        initialResult.Where(i => i.IsVisible(activeUser)).Select(i => i.Id));

                    query = new InternalItemsQuery(activeUser)
                    {
                        ItemIds = [.. itemIds],
                        IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Season],
                        IsPlayed = _config.ShowPlayed ? null : false
                    };
                    result = PrepareResult(query, activeUser, libraryManager);
                    resultsEmpty = result.Count == 0;
                }
            }

            // COLLECTIONS mode
            if (_config.Mode == "COLLECTIONS")
            {
                var remaining = _config.SelectedCollections.ToList();
                while (result.Count == 0 && remaining.Count > 0)
                {
                    var collectionGuid = Guid.Parse(remaining[new Random().Next(remaining.Count)]);
                    remaining.Remove(collectionGuid.ToString());
                    var collection = libraryManager.GetParentItem(collectionGuid, activeUser.Id);
                    if (collection is Folder f)
                    {
                        initialResult = f.GetChildren(activeUser, true);
                        var itemIds = initialResult.Select(i => i.Id).Distinct().ToArray();
                        var q = new InternalItemsQuery(activeUser)
                        {
                            ItemIds = itemIds,
                            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie],
                            MinCommunityRating = _config.MinimumRating,
                            MinCriticRating = _config.MinimumCriticRating,
                            MaxParentalRating = maximumParentRating,
                            HasParentalRating = mustHaveParentRating,
                            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                            IsPlayed = _config.ShowPlayed ? null : false,
                            Limit = _config.RandomMediaCount * 2
                        };
                        result = PrepareResult(q, activeUser, libraryManager);
                    }
                    resultsEmpty = result.Count == 0;
                }
            }

            // NEW mode
            if (_config.Mode == "NEW")
            {
                DateTime newEndDate = _config.NewTimeLimit switch
                {
                    "2month" => DateTime.Today.AddMonths(-2),
                    "6month" => DateTime.Today.AddMonths(-6),
                    "1year"  => DateTime.Today.AddYears(-1),
                    "2year"  => DateTime.Today.AddYears(-2),
                    "5year"  => DateTime.Today.AddYears(-5),
                    _        => DateTime.Today.AddMonths(-1)
                };

                var qItems = new InternalItemsQuery(activeUser)
                {
                    IncludeItemTypes = [BaseItemKind.Series],
                    MinCommunityRating = _config.MinimumRating,
                    MinCriticRating = _config.MinimumCriticRating,
                    MaxParentalRating = maximumParentRating,
                    HasParentalRating = mustHaveParentRating,
                    MinEndDate = newEndDate,
                    OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                    IsPlayed = _config.ShowPlayed ? null : false,
                    Limit = _config.RandomMediaCount
                };
                var resultItems = PrepareResult(qItems, activeUser, libraryManager);

                var qMovies = new InternalItemsQuery(activeUser)
                {
                    IncludeItemTypes = [BaseItemKind.Movie],
                    MinCommunityRating = _config.MinimumRating,
                    MinCriticRating = _config.MinimumCriticRating,
                    MaxParentalRating = maximumParentRating,
                    HasParentalRating = mustHaveParentRating,
                    MinPremiereDate = newEndDate,
                    OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                    IsPlayed = _config.ShowPlayed ? null : false,
                    Limit = _config.RandomMediaCount
                };
                var resultMovies = PrepareResult(qMovies, activeUser, libraryManager);

                result = resultItems.Concat(resultMovies).ToList();
                resultsEmpty = result.Count == 0;
            }

            // RANDOM or fallback
            if (_config.Mode == "RANDOM" || resultsEmpty)
            {
                query = new InternalItemsQuery(activeUser)
                {
                    IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie],
                    MinCommunityRating = _config.MinimumRating,
                    MinCriticRating = _config.MinimumCriticRating,
                    MaxParentalRating = maximumParentRating,
                    HasParentalRating = mustHaveParentRating,
                    OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                    IsPlayed = _config.ShowPlayed ? null : false,
                    Limit = _config.RandomMediaCount * 2
                };
                result = PrepareResult(query, activeUser, libraryManager);
            }

            // Build response
            items = new List<object>();
            foreach (var item in result)
            {
                var o = new Dictionary<string, object?>
                {
                    ["id"] = item.Id.ToString(),
                    ["name"] = item.Name,
                    ["tagline"] = item.Tagline,
                    ["official_rating"] = item.OfficialRating,
                    ["hasLogo"] = item.HasImage(MediaBrowser.Model.Entities.ImageType.Logo)
                };

                if (_config.ShowDescription) o["overview"] = item.Overview;
                if (_config.ShowRating && item.CriticRating.HasValue)   o["critic_rating"] = item.CriticRating;
                if (_config.ShowRating && item.CommunityRating.HasValue) o["community_rating"] = Math.Round((decimal)item.CommunityRating.Value, 2);

                items.Add(o);
            }

            response = new Dictionary<string, object>
            {
                ["favourites"] = items,
                ["autoplay"] = _config.EnableAutoplay,
                ["autoplayInterval"] = _config.AutoplayInterval * 1000,
                ["reduceImageSizes"] = _config.ReduceImageSize,
                ["bannerHeight"] = _config.BannerHeight
            };
            if (!string.IsNullOrWhiteSpace(_config.Heading)) response["heading"] = _config.Heading;

            return Ok(response);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "EditorsChoice: favourites error");
            return StatusCode(503);
        }
    }

    private List<BaseItem> PrepareResult(
        InternalItemsQuery query,
        Jellyfin.Data.Entities.User? activeUser,
        ILibraryManager libraryManager)
    {
        var initialResult = libraryManager.GetItemList(query);
        var result = new List<BaseItem>();
        var random = new Random();
        var max = initialResult.Count;

        for (int i = 0; i < _config.RandomMediaCount && i < max; i++)
        {
            var initItem = initialResult[random.Next(initialResult.Count)];
            var item = initItem;

            if (item.GetBaseItemKind() is BaseItemKind.Episode or BaseItemKind.Season)
            {
                item = item.GetParent();
                if (item.GetBaseItemKind() == BaseItemKind.Season)
                    item = item.GetParent();
            }

            var inFilteredLibrary =
                _config.FilteredLibraries.Length == 0 ||
                _config.FilteredLibraries.Any(id => item.GetAncestorIds().Contains(Guid.Parse(id)));

            if (item.IsVisible(activeUser)
                && !result.Contains(item)
                && inFilteredLibrary
                && !(! _config.ShowPlayed && item.IsPlayed(activeUser))
                && item.HasImage(MediaBrowser.Model.Entities.ImageType.Backdrop))
            {
                result.Add(item);
            }
            else
            {
                i--; max--;
            }
            initialResult.Remove(initItem);
        }

        return result;
    }

    [HttpPost("transform")]
    public ActionResult IndexTransformation([FromBody] PatchRequestPayload payload)
    {
        var net = Plugin.Instance!.ServerConfigurationManager.GetNetworkConfiguration();
        var basePath = string.IsNullOrWhiteSpace(net.BaseUrl) ? "" : "/" + net.BaseUrl.Trim('/');
        // use the same lower-case route as this controller
        var script = $"<script FileTransformation=\"true\" plugin=\"EditorsChoice\" defer=\"defer\" src=\"{basePath}/editorschoice/script\"></script>";
        var html = Regex.Replace(payload?.Contents ?? string.Empty, "(</body>)", $"{script}$1", RegexOptions.IgnoreCase);
        return Content(html, MediaTypeNames.Text.Html);
    }
}

public class PatchRequestPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
