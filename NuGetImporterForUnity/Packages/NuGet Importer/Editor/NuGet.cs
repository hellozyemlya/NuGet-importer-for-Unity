﻿#if ZIP_AVAILABLE

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using kumaS.NuGetImporter.Editor.DataClasses;

using UnityEditor;

using UnityEngine;

namespace kumaS.NuGetImporter.Editor
{
    /// <summary>
    /// <para>Class for using NuGet API.</para>
    /// <para>NuGetのAPIを使うためのクラス。</para>
    /// </summary>
    public static class NuGet
    {
        private static readonly List<string> searchQueryService = new List<string>() { "https://azuresearch-usnc.nuget.org/query" };
        private static string packageBaseAddress = "https://api.nuget.org/v3-flatcontainer/";
        private static string registrationsBaseUrl = "https://api.nuget.org/v3/registration5-gz-semver2/";
        private static readonly HttpClient client = new HttpClient(new HttpClientHandler() { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });

        /// <value>
        /// <para>Limit of search cache.</para>
        /// <para>検索のキャッシュの最大数。</para>
        /// </value>
        public static int searchCacheLimit = 500;
        private static readonly Dictionary<string, SearchResult> searchCache = new Dictionary<string, SearchResult>();
        private static readonly List<string> searchLog = new List<string>();
        private static readonly Dictionary<string, Task> gettingSearchs = new Dictionary<string, Task>();

        /// <value>
        /// <para>Limit of catalog cache.</para>
        /// <para>カタログのキャッシュの最大数。</para>
        /// </value>
        public static int catalogCacheLimit = 300;

        /// <value>
        /// <para>For test.</para>
        /// </value>
        internal static readonly Dictionary<string, Catalog> catalogCache = new Dictionary<string, Catalog>();
        private static readonly List<string> catalogLog = new List<string>();
        private static readonly Dictionary<string, Task<string>> gettingCatalogs = new Dictionary<string, Task<string>>();

        /// <summary>
        /// <para>Initialize the API endpoint.</para>
        /// <para>APIのエンドポイントを初期化する。</para>
        /// </summary>
        /// <returns>
        /// <c>Task</c>
        /// </returns>
        [InitializeOnLoadMethod]
        public static async Task InitializeAPIEndPoint()
        {
            var responseText = await client.GetStringAsync("https://api.nuget.org/v3/index.json");
            APIList apiList = JsonUtility.FromJson<APIList>(RefineJson(responseText));
            var searchQueryServices = new List<string>();
            foreach (Resource apiInfo in apiList.resources)
            {
                switch (apiInfo.nuget_type)
                {
                    case "SearchQueryService":
                        if (apiInfo.comment.Contains("primary"))
                        {
                            searchQueryServices.Insert(0, apiInfo.nuget_id);
                        }
                        else
                        {
                            searchQueryServices.Add(apiInfo.nuget_id);
                        }
                        break;
                    case "PackageBaseAddress/3.0.0":
                        packageBaseAddress = apiInfo.nuget_id;
                        break;
                    case "RegistrationsBaseUrl/3.6.0":
                        registrationsBaseUrl = apiInfo.nuget_id;
                        break;
                }
            }
        }

        /// <summary>
        /// <para>Delete the cache of search and catalog.</para>
        /// <para>カタログと検索結果のキャッシュを削除する。</para>
        /// </summary>
        public static void DeleteCache()
        {
            lock (searchCache)
            {
                searchCache.Clear();
                searchLog.Clear();
                gettingSearchs.Clear();
            }

            lock (catalogCache)
            {
                catalogCache.Clear();
                catalogLog.Clear();
                gettingCatalogs.Clear();
            }
        }

        /// <summary>
        /// <para>Search for packages.</para>
        /// <para>パッケージを検索する。</para>
        /// </summary>
        /// <param name="q">
        /// <para>Search word.</para>
        /// <para>検索語句。</para>
        /// </param>
        /// <param name="skip">
        /// <para>Number of packages to skip.</para>
        /// <para>スキップするパッケージの数。</para>
        /// </param>
        /// <param name="take">
        /// <para>Number of packages to take.</para>
        /// <para>取得するパッケージの数。</para>
        /// </param>
        /// <param name="prerelease">
        /// <para>Whether include prerelease.</para>
        /// <para>プリリリースを含めるか。</para>
        /// </param>
        /// <returns>
        /// <para>Search result.</para>
        /// <para>検索結果。</para>
        /// </returns>
        public static async Task<SearchResult> SearchPackage(string q = "", int skip = -1, int take = -1, bool prerelease = false)
        {
            var query = "";
            Action concat = () =>
            {
                query += query == "" ? "?" : "&";
            };

            if (q != "")
            {
                concat();
                query += "q=" + q;
            }
            if (skip > 0)
            {
                concat();
                query += "skip=" + skip;
            }
            if (take > 0)
            {
                concat();
                query += "take=" + take;
            }
            concat();
            query += "semVerLevel=2.0.0";
            if (prerelease)
            {
                concat();
                query += "prerelease=true";
            }
            lock (searchCache)
            {
                if (searchCache.ContainsKey(query))
                {
                    searchLog.Remove(query);
                    searchLog.Add(query);
                    return searchCache[query];
                }
            }
            var isGetting = false;
            lock (gettingSearchs)
            {
                isGetting = gettingSearchs.ContainsKey(query);
            }
            if (isGetting)
            {
                await gettingSearchs[query];
                return searchCache[query];
            }

            var task = Task.Run(async () =>
            {
                var index = 0;
                var responseText = "";
                while (true)
                {
                    try
                    {
                        responseText = await client.GetStringAsync(searchQueryService[index++] + query);
                        break;
                    }
                    catch (Exception)
                    {
                        if (index >= searchQueryService.Count)
                        {
                            throw;
                        }
                    }
                }
                SearchResult result = JsonUtility.FromJson<SearchResult>(RefineJson(responseText));
                lock (searchCache)
                {
                    searchCache.Add(query, result);
                    searchLog.Add(query);
                    while (searchCache.Count > searchCacheLimit && searchCache.Count > 0)
                    {
                        var delete = searchLog[0];
                        searchLog.RemoveAt(0);
                        searchCache.Remove(delete);
                    }
                }
            });
            lock (gettingSearchs)
            {
                gettingSearchs.Add(query, task);
            }
            await task;
            lock (gettingSearchs)
            {
                gettingSearchs.Remove(query);
            }

            lock (searchCache)
            {
                return searchCache[query];
            }
        }

        /// <summary>
        /// <para>Get the specified package.</para>
        /// <para>指定したパッケージを取得する。</para>
        /// </summary>
        /// <param name="packageName">
        /// <para>Package name.</para>
        /// <para>パッケージの名前。</para>
        /// </param>
        /// <param name="version">
        /// <para>Version.</para>
        /// <para>バージョン。</para>
        /// </param>
        /// <param name="savePath">
        /// <para>Destination directory. It will be saved as a .nupkg file in this directory.</para>
        /// <para>保存先のディレクトリ。このディレクトリ内に.nupkgファイルとして保存される。</para>
        /// </param>
        /// <returns>
        /// <para>Task</para>
        /// </returns>
        public static async Task GetPackage(string packageName, string version, string savePath)
        {
            using (var fileStream = new FileStream(Path.Combine(savePath, packageName.ToLowerInvariant() + "." + version.ToLowerInvariant() + ".nupkg"), FileMode.Create, FileAccess.Write, FileShare.None))
            using (Stream responseStream = await client.GetStreamAsync(packageBaseAddress + packageName.ToLowerInvariant() + "/" + version.ToLowerInvariant() + "/" + packageName.ToLowerInvariant() + "." + version.ToLowerInvariant() + ".nupkg"))
            {
                responseStream.CopyTo(fileStream);
            }
        }

        /// <summary>
        /// <para>Get the specified catalog.</para>
        /// <para>指定したカタログを所得する。</para>
        /// </summary>
        /// <param name="packageName">
        /// <para>Package name.</para>
        /// <para>パッケージの名前。</para>
        /// </param>
        /// <returns>
        /// <para>Specified catalog.</para>
        /// <para>指定したカタログ。</para>
        /// </returns>
        public static async Task<Catalog> GetCatalog(string packageName)
        {
            lock (catalogCache)
            {
                if (catalogCache.ContainsKey(packageName))
                {
                    catalogLog.Remove(packageName);
                    catalogLog.Add(packageName);
                    return catalogCache[packageName];
                }
            }
            var isGetting = false;
            lock (gettingCatalogs)
            {
                isGetting = gettingCatalogs.ContainsKey(packageName);
            }
            if (isGetting)
            {
                await gettingCatalogs[packageName];
                return catalogCache[packageName];
            }

            using (Task<string> request = client.GetStringAsync(registrationsBaseUrl + packageName.ToLowerInvariant() + "/index.json"))
            {
                lock (gettingCatalogs)
                {
                    gettingCatalogs.Add(packageName, request);
                }

                var responseText = await request;
                Catalog catalog = JsonUtility.FromJson<Catalog>(RefineJson(responseText));
                if (catalog.items[0].items == null)
                {
                    var tasks = new List<Task>();
                    for (var i = 0; i < catalog.items.Length; i++)
                    {
                        tasks.Add(GetItem(catalog, i));
                    }
                    await Task.WhenAll(tasks);
                }
                lock (catalogCache)
                {
                    catalogCache[packageName] = catalog;
                    catalogLog.Add(packageName);
                    while (catalogCache.Count > catalogCacheLimit && catalogCache.Count > 0)
                    {
                        var delete = catalogLog[0];
                        catalogLog.RemoveAt(0);
                        catalogCache.Remove(delete);
                    }
                }
                lock (gettingCatalogs)
                {
                    gettingCatalogs.Remove(packageName);
                }
                lock (catalogCache)
                {
                    return catalogCache[packageName];
                }
            }
        }

        private static async Task GetItem(Catalog catalog, int index)
        {
            var itemText = await client.GetStringAsync(catalog.items[index].nuget_id);
            catalog.items[index] = JsonUtility.FromJson<Item>(RefineJson(itemText));
        }

        private static string RefineJson(string json)
        {
            return json.Replace(@"""@id"":", @"""nuget_id"":").Replace(@"""@type"":", @"""nuget_type"":");
        }
    }
}

#endif