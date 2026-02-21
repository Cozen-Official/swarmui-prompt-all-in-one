using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;
using System.IO;
using System.Net.Http;
using System.Text;

namespace PromptAllInOne;

/// <summary>SwarmUI extension that provides the Prompt All-In-One UI for the generate tab.</summary>
public class PromptAllInOneExtension : Extension
{
    public static string ExtFolder;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public override void OnInit()
    {
        ExtFolder = FilePath;
        ScriptFiles.Add("javascript/main.entry.js");
        StyleSheetFiles.Add("style.css");
    }

    public override void OnPreLaunch()
    {
        WebServer.WebApp.MapGet("/physton_prompt/get_config", GetConfig);
        WebServer.WebApp.MapGet("/physton_prompt/get_version", GetVersion);
        WebServer.WebApp.MapGet("/physton_prompt/get_data", GetData);
        WebServer.WebApp.MapGet("/physton_prompt/get_datas", GetDatas);
        WebServer.WebApp.MapPost("/physton_prompt/set_data", SetData);
        WebServer.WebApp.MapPost("/physton_prompt/set_datas", SetDatas);
        WebServer.WebApp.MapGet("/physton_prompt/get_data_list_item", GetDataListItem);
        WebServer.WebApp.MapPost("/physton_prompt/push_data_list", PushDataList);
        WebServer.WebApp.MapPost("/physton_prompt/pop_data_list", PopDataList);
        WebServer.WebApp.MapPost("/physton_prompt/shift_data_list", ShiftDataList);
        WebServer.WebApp.MapPost("/physton_prompt/remove_data_list", RemoveDataList);
        WebServer.WebApp.MapPost("/physton_prompt/clear_data_list", ClearDataList);
        WebServer.WebApp.MapGet("/physton_prompt/get_histories", GetHistories);
        WebServer.WebApp.MapGet("/physton_prompt/get_favorites", GetFavorites);
        WebServer.WebApp.MapPost("/physton_prompt/push_history", PushHistory);
        WebServer.WebApp.MapPost("/physton_prompt/push_favorite", PushFavorite);
        WebServer.WebApp.MapPost("/physton_prompt/move_up_favorite", MoveUpFavorite);
        WebServer.WebApp.MapPost("/physton_prompt/move_down_favorite", MoveDownFavorite);
        WebServer.WebApp.MapGet("/physton_prompt/get_latest_history", GetLatestHistory);
        WebServer.WebApp.MapPost("/physton_prompt/set_history", SetHistory);
        WebServer.WebApp.MapPost("/physton_prompt/set_history_name", SetHistoryName);
        WebServer.WebApp.MapPost("/physton_prompt/set_favorite_name", SetFavoriteName);
        WebServer.WebApp.MapPost("/physton_prompt/dofavorite", DoFavorite);
        WebServer.WebApp.MapPost("/physton_prompt/unfavorite", UnFavorite);
        WebServer.WebApp.MapPost("/physton_prompt/delete_history", DeleteHistory);
        WebServer.WebApp.MapPost("/physton_prompt/delete_histories", DeleteHistories);
        WebServer.WebApp.MapPost("/physton_prompt/translate", Translate);
        WebServer.WebApp.MapPost("/physton_prompt/translates", Translates);
        WebServer.WebApp.MapGet("/physton_prompt/get_csvs", GetCsvs);
        WebServer.WebApp.MapGet("/physton_prompt/get_csv", GetCsv);
        WebServer.WebApp.MapGet("/physton_prompt/styles", GetStyles);
        WebServer.WebApp.MapGet("/physton_prompt/get_extension_css_list", GetExtensionCssList);
        WebServer.WebApp.MapGet("/physton_prompt/get_extra_networks", GetExtraNetworks);
        WebServer.WebApp.MapGet("/physton_prompt/get_extensions", GetExtensions);
        WebServer.WebApp.MapPost("/physton_prompt/token_counter", TokenCounter);
        WebServer.WebApp.MapGet("/physton_prompt/get_group_tags", GetGroupTags);
        WebServer.WebApp.MapPost("/physton_prompt/gen_openai", GenOpenAI);
        WebServer.WebApp.MapPost("/physton_prompt/install_package", InstallPackage);
    }

    // ========== Storage ==========

    private static string StoragePath => Path.Combine(ExtFolder, "storage");

    private static string GetStorageFilePath(string key)
    {
        Directory.CreateDirectory(StoragePath);
        return Path.Combine(StoragePath, $"{SanitizeKey(key)}.json");
    }

    private static string SanitizeKey(string key) =>
        string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static readonly Dictionary<string, SemaphoreSlim> StorageLocks = [];
    private static SemaphoreSlim GetLock(string key)
    {
        lock (StorageLocks)
        {
            if (!StorageLocks.TryGetValue(key, out SemaphoreSlim sem))
            {
                sem = new SemaphoreSlim(1, 1);
                StorageLocks[key] = sem;
            }
            return sem;
        }
    }

    private static JToken StorageGet(string key)
    {
        string path = GetStorageFilePath(key);
        if (!File.Exists(path)) return null;
        string text = File.ReadAllText(path, Encoding.UTF8).Trim();
        if (string.IsNullOrEmpty(text)) return null;
        try { return JToken.Parse(text); }
        catch { return null; }
    }

    private static async Task StorageSet(string key, JToken data)
    {
        SemaphoreSlim sem = GetLock(key);
        await sem.WaitAsync();
        try
        {
            string path = GetStorageFilePath(key);
            await File.WriteAllTextAsync(path, data.ToString(Formatting.Indented), Encoding.UTF8);
        }
        finally { sem.Release(); }
    }

    private static JArray StorageGetList(string key)
    {
        JToken data = StorageGet(key);
        return data as JArray ?? [];
    }

    // ========== Config ==========

    private static async Task GetConfig(HttpContext context)
    {
        string i18nPath = Path.Combine(ExtFolder, "i18n.json");
        string translateApisPath = Path.Combine(ExtFolder, "translate_apis.json");
        JObject i18n = File.Exists(i18nPath) ? JObject.Parse(await File.ReadAllTextAsync(i18nPath)) : new JObject();
        JObject translateApis = File.Exists(translateApisPath) ? JObject.Parse(await File.ReadAllTextAsync(translateApisPath)) : new JObject();
        JObject result = new()
        {
            ["i18n"] = i18n,
            ["translate_apis"] = translateApis,
            ["packages_state"] = new JObject(),
            ["python"] = ""
        };
        await WriteJson(context, result);
    }

    private static async Task GetVersion(HttpContext context)
    {
        await WriteJson(context, new JObject { ["version"] = "swarmui", ["latest_version"] = "swarmui" });
    }

    // ========== Key-Value Storage ==========

    private static async Task GetData(HttpContext context)
    {
        string key = context.Request.Query["key"].ToString();
        JToken data = StorageGet(key);
        await WriteJson(context, new JObject { ["data"] = data });
    }

    private static async Task GetDatas(HttpContext context)
    {
        string keysParam = context.Request.Query["keys"].ToString();
        string[] keys = keysParam.Split(',');
        JObject datas = [];
        foreach (string key in keys) datas[key] = StorageGet(key);
        await WriteJson(context, new JObject { ["datas"] = datas });
    }

    private static async Task SetData(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("key"))
        {
            await WriteJson(context, new JObject { ["success"] = false, ["message"] = "key is required" });
            return;
        }
        await StorageSet(body["key"].ToString(), body["data"] ?? JValue.CreateNull());
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task SetDatas(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null)
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        foreach (KeyValuePair<string, JToken> kvp in body)
            await StorageSet(kvp.Key, kvp.Value ?? JValue.CreateNull());
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task GetDataListItem(HttpContext context)
    {
        string key = context.Request.Query["key"].ToString();
        int index = int.Parse(context.Request.Query["index"].ToString());
        JArray list = StorageGetList(key);
        JToken item = index >= 0 && index < list.Count ? list[index] : JValue.CreateNull();
        await WriteJson(context, new JObject { ["item"] = item });
    }

    private static async Task PushDataList(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("key"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string key = body["key"].ToString();
        SemaphoreSlim sem = GetLock(key);
        await sem.WaitAsync();
        try
        {
            JArray list = StorageGetList(key);
            list.Add(body["item"] ?? JValue.CreateNull());
            await File.WriteAllTextAsync(GetStorageFilePath(key), list.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task PopDataList(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("key"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string key = body["key"].ToString();
        SemaphoreSlim sem = GetLock(key);
        await sem.WaitAsync();
        JToken item = JValue.CreateNull();
        try
        {
            JArray list = StorageGetList(key);
            if (list.Count > 0)
            {
                item = list[^1];
                list.RemoveAt(list.Count - 1);
                await File.WriteAllTextAsync(GetStorageFilePath(key), list.ToString(Formatting.Indented));
            }
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true, ["item"] = item });
    }

    private static async Task ShiftDataList(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("key"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string key = body["key"].ToString();
        SemaphoreSlim sem = GetLock(key);
        await sem.WaitAsync();
        JToken item = JValue.CreateNull();
        try
        {
            JArray list = StorageGetList(key);
            if (list.Count > 0)
            {
                item = list[0];
                list.RemoveAt(0);
                await File.WriteAllTextAsync(GetStorageFilePath(key), list.ToString(Formatting.Indented));
            }
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true, ["item"] = item });
    }

    private static async Task RemoveDataList(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("key"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string key = body["key"].ToString();
        int index = (int)body["index"];
        SemaphoreSlim sem = GetLock(key);
        await sem.WaitAsync();
        try
        {
            JArray list = StorageGetList(key);
            if (index >= 0 && index < list.Count) list.RemoveAt(index);
            await File.WriteAllTextAsync(GetStorageFilePath(key), list.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task ClearDataList(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("key"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string key = body["key"].ToString();
        await StorageSet(key, new JArray());
        await WriteJson(context, new JObject { ["success"] = true });
    }

    // ========== History / Favorites ==========

    private const int MaxHistories = 100;

    private static JArray LoadHistories(string type)
    {
        JToken data = StorageGet($"history.{type}");
        return data as JArray ?? [];
    }

    private static JArray LoadFavorites(string type)
    {
        JToken data = StorageGet($"favorite.{type}");
        return data as JArray ?? [];
    }

    private static bool IsFavorite(string type, string id)
    {
        JArray favorites = LoadFavorites(type);
        return favorites.Any(f => f["id"]?.ToString() == id);
    }

    private static async Task GetHistories(HttpContext context)
    {
        string type = context.Request.Query["type"].ToString();
        JArray histories = LoadHistories(type);
        foreach (JObject h in histories.Cast<JObject>())
            h["is_favorite"] = IsFavorite(type, h["id"]?.ToString());
        await WriteJson(context, new JObject { ["histories"] = histories });
    }

    private static async Task GetFavorites(HttpContext context)
    {
        string type = context.Request.Query["type"].ToString();
        await WriteJson(context, new JObject { ["favorites"] = LoadFavorites(type) });
    }

    private static JObject CreateHistoryItem(JObject body)
    {
        return new JObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["name"] = body["name"]?.ToString() ?? "",
            ["tags"] = body["tags"] ?? new JArray(),
            ["prompt"] = body["prompt"]?.ToString() ?? ""
        };
    }

    private static async Task PushHistory(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        SemaphoreSlim sem = GetLock($"history.{type}");
        await sem.WaitAsync();
        try
        {
            JArray histories = LoadHistories(type);
            while (histories.Count >= MaxHistories) histories.RemoveAt(0);
            histories.Add(CreateHistoryItem(body));
            await File.WriteAllTextAsync(GetStorageFilePath($"history.{type}"), histories.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task PushFavorite(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        SemaphoreSlim sem = GetLock($"favorite.{type}");
        await sem.WaitAsync();
        try
        {
            JArray favorites = LoadFavorites(type);
            favorites.Add(CreateHistoryItem(body));
            await File.WriteAllTextAsync(GetStorageFilePath($"favorite.{type}"), favorites.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task MoveUpFavorite(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type") || !body.ContainsKey("id"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        string id = body["id"].ToString();
        SemaphoreSlim sem = GetLock($"favorite.{type}");
        await sem.WaitAsync();
        try
        {
            JArray favorites = LoadFavorites(type);
            int idx = favorites.IndexOf(favorites.FirstOrDefault(f => f["id"]?.ToString() == id));
            if (idx > 0) (favorites[idx - 1], favorites[idx]) = (favorites[idx], favorites[idx - 1]);
            await File.WriteAllTextAsync(GetStorageFilePath($"favorite.{type}"), favorites.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task MoveDownFavorite(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type") || !body.ContainsKey("id"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        string id = body["id"].ToString();
        SemaphoreSlim sem = GetLock($"favorite.{type}");
        await sem.WaitAsync();
        try
        {
            JArray favorites = LoadFavorites(type);
            int idx = favorites.IndexOf(favorites.FirstOrDefault(f => f["id"]?.ToString() == id));
            if (idx >= 0 && idx < favorites.Count - 1) (favorites[idx + 1], favorites[idx]) = (favorites[idx], favorites[idx + 1]);
            await File.WriteAllTextAsync(GetStorageFilePath($"favorite.{type}"), favorites.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task GetLatestHistory(HttpContext context)
    {
        string type = context.Request.Query["type"].ToString();
        JArray histories = LoadHistories(type);
        JToken latest = histories.Count > 0 ? histories[^1] : JValue.CreateNull();
        await WriteJson(context, new JObject { ["history"] = latest });
    }

    private static async Task SetHistory(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type") || !body.ContainsKey("id"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        string id = body["id"].ToString();
        SemaphoreSlim sem = GetLock($"history.{type}");
        await sem.WaitAsync();
        try
        {
            JArray histories = LoadHistories(type);
            JObject item = histories.FirstOrDefault(h => h["id"]?.ToString() == id) as JObject;
            if (item != null)
            {
                item["tags"] = body["tags"] ?? item["tags"];
                item["prompt"] = body["prompt"]?.ToString() ?? item["prompt"]?.ToString();
                item["name"] = body["name"]?.ToString() ?? item["name"]?.ToString();
            }
            await File.WriteAllTextAsync(GetStorageFilePath($"history.{type}"), histories.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task SetHistoryName(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type") || !body.ContainsKey("id") || !body.ContainsKey("name"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        string id = body["id"].ToString();
        string name = body["name"].ToString();
        SemaphoreSlim sem = GetLock($"history.{type}");
        await sem.WaitAsync();
        try
        {
            JArray items = LoadHistories(type);
            JObject item = items.FirstOrDefault(h => h["id"]?.ToString() == id) as JObject;
            if (item != null) item["name"] = name;
            await File.WriteAllTextAsync(GetStorageFilePath($"history.{type}"), items.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task SetFavoriteName(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type") || !body.ContainsKey("id") || !body.ContainsKey("name"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        string id = body["id"].ToString();
        string name = body["name"].ToString();
        SemaphoreSlim sem = GetLock($"favorite.{type}");
        await sem.WaitAsync();
        try
        {
            JArray items = LoadFavorites(type);
            JObject item = items.FirstOrDefault(h => h["id"]?.ToString() == id) as JObject;
            if (item != null) item["name"] = name;
            await File.WriteAllTextAsync(GetStorageFilePath($"favorite.{type}"), items.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task DoFavorite(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type") || !body.ContainsKey("id"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        string id = body["id"].ToString();
        JArray histories = LoadHistories(type);
        JObject item = histories.FirstOrDefault(h => h["id"]?.ToString() == id) as JObject;
        if (item != null)
        {
            SemaphoreSlim sem = GetLock($"favorite.{type}");
            await sem.WaitAsync();
            try
            {
                JArray favorites = LoadFavorites(type);
                if (!favorites.Any(f => f["id"]?.ToString() == id))
                    favorites.Add(item.DeepClone());
                await File.WriteAllTextAsync(GetStorageFilePath($"favorite.{type}"), favorites.ToString(Formatting.Indented));
            }
            finally { sem.Release(); }
        }
        await WriteJson(context, new JObject { ["success"] = item != null });
    }

    private static async Task UnFavorite(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type") || !body.ContainsKey("id"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        string id = body["id"].ToString();
        SemaphoreSlim sem = GetLock($"favorite.{type}");
        await sem.WaitAsync();
        try
        {
            JArray favorites = LoadFavorites(type);
            JToken item = favorites.FirstOrDefault(f => f["id"]?.ToString() == id);
            if (item != null) favorites.Remove(item);
            await File.WriteAllTextAsync(GetStorageFilePath($"favorite.{type}"), favorites.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task DeleteHistory(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type") || !body.ContainsKey("id"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        string id = body["id"].ToString();
        SemaphoreSlim sem = GetLock($"history.{type}");
        await sem.WaitAsync();
        try
        {
            JArray histories = LoadHistories(type);
            JToken item = histories.FirstOrDefault(h => h["id"]?.ToString() == id);
            if (item != null) histories.Remove(item);
            await File.WriteAllTextAsync(GetStorageFilePath($"history.{type}"), histories.ToString(Formatting.Indented));
        }
        finally { sem.Release(); }
        await WriteJson(context, new JObject { ["success"] = true });
    }

    private static async Task DeleteHistories(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || !body.ContainsKey("type"))
        {
            await WriteJson(context, new JObject { ["success"] = false });
            return;
        }
        string type = body["type"].ToString();
        await StorageSet($"history.{type}", new JArray());
        await WriteJson(context, new JObject { ["success"] = true });
    }

    // ========== Translation ==========

    private static async Task Translate(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null)
        {
            await WriteJson(context, new JObject { ["success"] = false, ["message"] = "Invalid request" });
            return;
        }
        JObject result = await DoTranslate(body);
        await WriteJson(context, result);
    }

    private static async Task Translates(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || body["texts"] == null)
        {
            await WriteJson(context, new JObject { ["success"] = false, ["message"] = "texts is required" });
            return;
        }
        string fromLang = body["from_lang"]?.ToString() ?? "auto";
        string toLang = body["to_lang"]?.ToString() ?? "en";
        string api = body["api"]?.ToString() ?? "";
        JObject apiConfig = body["api_config"] as JObject ?? [];
        JArray results = [];
        foreach (JToken text in body["texts"] as JArray ?? [])
        {
            JObject req = new()
            {
                ["text"] = text.ToString(),
                ["from_lang"] = fromLang,
                ["to_lang"] = toLang,
                ["api"] = api,
                ["api_config"] = apiConfig
            };
            JObject res = await DoTranslate(req);
            results.Add(res["translated_text"] ?? "");
        }
        await WriteJson(context, new JObject { ["success"] = true, ["translated_text"] = results });
    }

    private static async Task<JObject> DoTranslate(JObject body)
    {
        string text = body["text"]?.ToString() ?? "";
        string fromLang = body["from_lang"]?.ToString() ?? "auto";
        string toLang = body["to_lang"]?.ToString() ?? "en";
        string api = body["api"]?.ToString() ?? "";
        JObject apiConfig = body["api_config"] as JObject ?? [];

        JObject Failure(string msg) => new()
        {
            ["success"] = false, ["message"] = msg,
            ["text"] = text, ["translated_text"] = "", ["from_lang"] = fromLang, ["to_lang"] = toLang, ["api"] = api
        };
        JObject Success(string translated) => new()
        {
            ["success"] = true, ["message"] = "",
            ["text"] = text, ["translated_text"] = translated, ["from_lang"] = fromLang, ["to_lang"] = toLang, ["api"] = api
        };

        try
        {
            string translated = api switch
            {
                "google" => await TranslateGoogle(text, fromLang, toLang, apiConfig),
                "baidu" => await TranslateBaidu(text, fromLang, toLang, apiConfig),
                "deepl" => await TranslateDeepL(text, fromLang, toLang, apiConfig),
                "openai" => await TranslateOpenAI(text, fromLang, toLang, apiConfig),
                "youdao" => await TranslateYouDao(text, fromLang, toLang, apiConfig),
                _ => throw new NotSupportedException($"Translation API '{api}' is not supported in SwarmUI mode.")
            };
            return Success(translated);
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
    }

    private static async Task<string> TranslateGoogle(string text, string from, string to, JObject config)
    {
        string apiKey = config["api_key"]?.ToString() ?? throw new Exception("API Key is required");
        string url = $"https://translation.googleapis.com/language/translate/v2/?key={Uri.EscapeDataString(apiKey)}";
        JObject payload = new() { ["q"] = text, ["source"] = from == "auto" ? "" : from, ["target"] = to, ["format"] = "text" };
        HttpResponseMessage response = await HttpClient.PostAsync(url,
            new StringContent(payload.ToString(), Encoding.UTF8, "application/json"));
        string json = await response.Content.ReadAsStringAsync();
        JObject result = JObject.Parse(json);
        if (result["error"] != null) throw new Exception(result["error"]?["message"]?.ToString() ?? "Google translation error");
        string translated = result["data"]?["translations"]?[0]?["translatedText"]?.ToString()
            ?? throw new Exception("Unexpected response from Google translation API");
        return translated;
    }

    private static async Task<string> TranslateBaidu(string text, string from, string to, JObject config)
    {
        string appId = config["app_id"]?.ToString() ?? throw new Exception("App ID is required");
        string apiKey = config["api_key"]?.ToString() ?? throw new Exception("API Key is required");
        byte[] saltBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4);
        string salt = (10000 + (BitConverter.ToUInt32(saltBytes, 0) % 90000)).ToString();
        string sign = ComputeMd5($"{appId}{text}{salt}{apiKey}");
        string bFrom = from == "auto" ? "auto" : from;
        string url = $"https://fanyi-api.baidu.com/api/trans/vip/translate?" +
                     $"q={Uri.EscapeDataString(text)}&from={bFrom}&to={to}&appid={appId}&salt={salt}&sign={sign}";
        string json = await HttpClient.GetStringAsync(url);
        JObject result = JObject.Parse(json);
        if (result["error_code"] != null) throw new Exception(result["error_msg"]?.ToString() ?? "Baidu translation error");
        string translated = result["trans_result"]?[0]?["dst"]?.ToString()
            ?? throw new Exception("Unexpected response from Baidu translation API");
        return translated;
    }

    private static async Task<string> TranslateDeepL(string text, string from, string to, JObject config)
    {
        string authKey = config["auth_key"]?.ToString() ?? throw new Exception("Auth Key is required");
        bool isFree = authKey.EndsWith(":fx");
        string baseUrl = isFree ? "https://api-free.deepl.com" : "https://api.deepl.com";
        JObject payload = new()
        {
            ["text"] = new JArray { text },
            ["target_lang"] = to.ToUpperInvariant(),
        };
        if (from != "auto") payload["source_lang"] = from.ToUpperInvariant();
        HttpRequestMessage req = new(HttpMethod.Post, $"{baseUrl}/v2/translate")
        {
            Headers = { { "Authorization", $"DeepL-Auth-Key {authKey}" } },
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        HttpResponseMessage response = await HttpClient.SendAsync(req);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        JObject result = JObject.Parse(json);
        if (result["message"] != null) throw new Exception(result["message"].ToString());
        string translated = result["translations"]?[0]?["text"]?.ToString()
            ?? throw new Exception("Unexpected response from DeepL translation API");
        return translated;
    }

    private static async Task<string> TranslateOpenAI(string text, string from, string to, JObject config)
    {
        string apiKey = config["api_key"]?.ToString() ?? throw new Exception("API Key is required");
        string model = config["model"]?.ToString() ?? "gpt-3.5-turbo";
        string baseUrl = config["base_url"]?.ToString() ?? "https://api.openai.com/v1";
        string prompt = $"Translate the following text from {from} to {to}. Return only the translated text without any explanation:\n{text}";
        JObject payload = new()
        {
            ["model"] = model,
            ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } }
        };
        HttpRequestMessage req = new(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Headers = { { "Authorization", $"Bearer {apiKey}" } },
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        HttpResponseMessage response = await HttpClient.SendAsync(req);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        JObject result = JObject.Parse(json);
        if (result["error"] != null) throw new Exception(result["error"]?["message"]?.ToString() ?? "OpenAI translation error");
        string translated = result["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim()
            ?? throw new Exception("Unexpected response from OpenAI translation API");
        return translated;
    }

    private static async Task<string> TranslateYouDao(string text, string from, string to, JObject config)
    {
        string appKey = config["app_key"]?.ToString() ?? throw new Exception("App Key is required");
        string appSecret = config["app_secret"]?.ToString() ?? throw new Exception("App Secret is required");
        string salt = Guid.NewGuid().ToString("N")[..8];
        string curTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string input = text.Length <= 20 ? text : $"{text[..10]}{text.Length}{text[^10..]}";
        string sign = ComputeSha256($"{appKey}{input}{salt}{curTime}{appSecret}");
        JObject payload = new()
        {
            ["q"] = text, ["from"] = from == "auto" ? "auto" : from,
            ["to"] = to, ["appKey"] = appKey, ["salt"] = salt,
            ["sign"] = sign, ["signType"] = "v3", ["curtime"] = curTime
        };
        FormUrlEncodedContent formContent = new(payload.Properties().ToDictionary(p => p.Name, p => p.Value.ToString()));
        HttpResponseMessage response = await HttpClient.PostAsync("https://openapi.youdao.com/api", formContent);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        JObject result = JObject.Parse(json);
        if (result["errorCode"]?.ToString() != "0") throw new Exception($"YouDao error: {result["errorCode"]}");
        string translated = result["translation"]?[0]?.ToString()
            ?? throw new Exception("Unexpected response from YouDao translation API");
        return translated;
    }

    private static string ComputeMd5(string input)
    {
        byte[] bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ComputeSha256(string input)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ========== CSV Files ==========

    private static async Task GetCsvs(HttpContext context)
    {
        string tagsPath = Path.Combine(ExtFolder, "tags");
        List<string> csvs = [];
        if (Directory.Exists(tagsPath))
        {
            foreach (string f in Directory.GetFiles(tagsPath, "*.csv"))
                csvs.Add(Path.GetFileNameWithoutExtension(f));
        }
        await WriteJson(context, new JObject { ["csvs"] = new JArray(csvs) });
    }

    private static async Task GetCsv(HttpContext context)
    {
        string key = context.Request.Query["key"].ToString();
        string tagsPath = Path.GetFullPath(Path.Combine(ExtFolder, "tags"));
        string filePath = Path.GetFullPath(Path.Combine(tagsPath, $"{SanitizeKey(key)}.csv"));
        if (!File.Exists(filePath) || !filePath.StartsWith(tagsPath + Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 404;
            return;
        }
        context.Response.ContentType = "text/csv";
        context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(filePath)}\"");
        await context.Response.SendFileAsync(filePath);
    }

    // ========== Styles ==========

    private static async Task GetStyles(HttpContext context)
    {
        string file = context.Request.Query["file"].ToString();
        if (string.IsNullOrEmpty(file))
        {
            context.Response.StatusCode = 400;
            return;
        }
        string stylesPath = Path.GetFullPath(Path.Combine(ExtFolder, "styles"));
        string filePath = Path.GetFullPath(Path.Combine(stylesPath, file));
        if ((!filePath.StartsWith(stylesPath + Path.DirectorySeparatorChar) && filePath != stylesPath) || !File.Exists(filePath))
        {
            context.Response.StatusCode = 404;
            return;
        }
        context.Response.ContentType = "text/css";
        await context.Response.SendFileAsync(filePath);
    }

    private static async Task GetExtensionCssList(HttpContext context)
    {
        string extensionsPath = Path.Combine(ExtFolder, "styles", "extensions");
        JArray cssList = [];
        if (Directory.Exists(extensionsPath))
        {
            foreach (string dir in Directory.GetDirectories(extensionsPath))
            {
                string manifestPath = Path.Combine(dir, "manifest.json");
                string stylePath = Path.Combine(dir, "style.min.css");
                if (!File.Exists(manifestPath) || !File.Exists(stylePath)) continue;
                string manifest = await File.ReadAllTextAsync(manifestPath);
                string dirName = Path.GetFileName(dir);
                JToken selected = StorageGet($"extensionSelect.{dirName}");
                cssList.Add(new JObject
                {
                    ["dir"] = dirName,
                    ["dataName"] = $"extensionSelect.{dirName}",
                    ["selected"] = selected ?? false,
                    ["manifest"] = manifest,
                    ["style"] = $"extensions/{dirName}/style.min.css"
                });
            }
        }
        await WriteJson(context, new JObject { ["css_list"] = cssList });
    }

    // ========== Extra Networks / Extensions (SwarmUI doesn't use A1111's extra networks) ==========

    private static async Task GetExtraNetworks(HttpContext context)
    {
        await WriteJson(context, new JObject { ["extra_networks"] = new JArray() });
    }

    private static async Task GetExtensions(HttpContext context)
    {
        await WriteJson(context, new JObject { ["extends"] = new JArray() });
    }

    // ========== Token Counter ==========

    private static async Task TokenCounter(HttpContext context)
    {
        JObject body = await ReadJson(context);
        string text = body?["text"]?.ToString() ?? "";
        // Simple token estimation: count words/tokens split by common separators.
        // maxLength is 75, matching CLIP's 75-token limit used by Stable Diffusion.
        int tokenCount = EstimateTokens(text);
        int maxLength = 75;
        await WriteJson(context, new JObject { ["token_count"] = tokenCount, ["max_length"] = maxLength });
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        // Simple estimation: split by common separators
        return text.Split([' ', ',', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
    }

    // ========== Group Tags ==========

    private static async Task GetGroupTags(HttpContext context)
    {
        string lang = context.Request.Query["lang"].ToString();
        string groupTagsPath = Path.Combine(ExtFolder, "group_tags");
        string filePath = Path.Combine(groupTagsPath, $"{SanitizeKey(lang)}.yaml");
        if (!File.Exists(filePath))
        {
            // fall back to default
            filePath = Directory.Exists(groupTagsPath) ? Directory.GetFiles(groupTagsPath, "*.yaml").FirstOrDefault() : null;
        }
        string content = filePath != null && File.Exists(filePath) ? await File.ReadAllTextAsync(filePath) : "";
        await WriteJson(context, new JObject { ["tags"] = content });
    }

    // ========== OpenAI Generation ==========

    private static async Task GenOpenAI(HttpContext context)
    {
        JObject body = await ReadJson(context);
        if (body == null || body["messages"] == null)
        {
            await WriteJson(context, new JObject { ["success"] = false, ["message"] = "messages is required" });
            return;
        }
        JObject apiConfig = body["api_config"] as JObject ?? [];
        string apiKey = apiConfig["api_key"]?.ToString() ?? "";
        string model = apiConfig["model"]?.ToString() ?? "gpt-3.5-turbo";
        string baseUrl = apiConfig["base_url"]?.ToString() ?? "https://api.openai.com/v1";
        if (string.IsNullOrEmpty(apiKey))
        {
            await WriteJson(context, new JObject { ["success"] = false, ["message"] = "API Key is required" });
            return;
        }
        try
        {
            JObject payload = new() { ["model"] = model, ["messages"] = body["messages"] };
            HttpRequestMessage req = new(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Headers = { { "Authorization", $"Bearer {apiKey}" } },
                Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
            };
            HttpResponseMessage response = await HttpClient.SendAsync(req);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();
            JObject result = JObject.Parse(json);
            if (result["error"] != null) throw new Exception(result["error"]?["message"]?.ToString() ?? "OpenAI error");
            string content2 = result["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim()
                ?? throw new Exception("Unexpected response from OpenAI API");
            await WriteJson(context, new JObject { ["success"] = true, ["result"] = content2 });
        }
        catch (Exception ex)
        {
            await WriteJson(context, new JObject { ["success"] = false, ["message"] = ex.Message });
        }
    }

    // ========== Install Package (not applicable to SwarmUI) ==========

    private static async Task InstallPackage(HttpContext context)
    {
        await WriteJson(context, new JObject { ["result"] = "Package installation is not supported in SwarmUI mode." });
    }

    // ========== Helpers ==========

    private static async Task<JObject> ReadJson(HttpContext context)
    {
        try
        {
            using StreamReader reader = new(context.Request.Body, leaveOpen: true);
            string body = await reader.ReadToEndAsync();
            return JObject.Parse(body);
        }
        catch { return null; }
    }

    private static async Task WriteJson(HttpContext context, JObject data)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = 200;
        await context.Response.WriteAsync(data.ToString(Formatting.None));
    }
}
