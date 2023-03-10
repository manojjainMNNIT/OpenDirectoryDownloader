using Newtonsoft.Json;
using NLog;
using OpenDirectoryDownloader.Shared;
using OpenDirectoryDownloader.Shared.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace OpenDirectoryDownloader.Site.GoIndex.Go2Index
{
    public static class Go2IndexParser
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const string FolderMimeType = "application/vnd.google-apps.folder";
        const string Parser = "Go2Index";
        static readonly RateLimiter RateLimiter = new RateLimiter(1, TimeSpan.FromSeconds(1));

        public static async Task<WebDirectory> ParseIndex(HttpClient httpClient, WebDirectory webDirectory)
        {
            try
            {
                if (!OpenDirectoryIndexer.Session.Parameters.ContainsKey(Constants.Parameters_Password))
                {
                    Console.WriteLine($"{Parser} will always be indexed at a maximum rate of 1 per second, else you will run into problems and errors.");
                    Logger.Info($"{Parser} will always be indexed at a maximum rate of 1 per second, else you will run into problems and errors.");

                    Console.WriteLine("Check if password is needed...");
                    Logger.Info("Check if password is needed...");
                    OpenDirectoryIndexer.Session.Parameters[Constants.Parameters_Password] = null;

                    HttpResponseMessage httpResponseMessage = await httpClient.PostAsync(webDirectory.Uri, new StringContent(JsonConvert.SerializeObject(new Dictionary<string, object>
                    {
                        { "page_index", 0 },
                        { "page_token", null },
                        { "password", OpenDirectoryIndexer.Session.Parameters[Constants.Parameters_Password] },
                        { "q", "" }
                    })));

                    Go2IndexResponse indexResponse = null;

                    if (httpResponseMessage.IsSuccessStatusCode)
                    {
                        string responseJson = await httpResponseMessage.Content.ReadAsStringAsync();
                        indexResponse = Go2IndexResponse.FromJson(responseJson);

                        if (indexResponse.Error?.Code == (int)HttpStatusCode.Unauthorized)
                        {
                            Console.WriteLine("Directory is password protected, please enter password:");
                            Logger.Info("Directory is password protected, please enter password.");

                            OpenDirectoryIndexer.Session.Parameters["GoIndex_Password"] = Console.ReadLine();

                            Console.WriteLine($"Using password: {OpenDirectoryIndexer.Session.Parameters[Constants.Parameters_Password]}");
                            Logger.Info($"Using password: {OpenDirectoryIndexer.Session.Parameters[Constants.Parameters_Password]}");

                            httpResponseMessage = await httpClient.PostAsync(webDirectory.Uri, new StringContent(JsonConvert.SerializeObject(new Dictionary<string, object>
                            {
                                { "page_index", 0 },
                                { "page_token", null },
                                { "password", OpenDirectoryIndexer.Session.Parameters[Constants.Parameters_Password] },
                                { "q", "" }
                            })));

                            if (httpResponseMessage.IsSuccessStatusCode)
                            {
                                responseJson = await httpResponseMessage.Content.ReadAsStringAsync();
                                indexResponse = Go2IndexResponse.FromJson(responseJson);
                            }
                        }
                    }

                    if (indexResponse is null)
                    {
                        Console.WriteLine("Error. Invalid response. Stopping.");
                        Logger.Error("Error. Invalid response. Stopping.");
                    }
                    else
                    {
                        if (indexResponse.Error == null)
                        {
                            Console.WriteLine("Password OK!");
                            Logger.Info("Password OK!");

                            webDirectory = await ScanIndexAsync(httpClient, webDirectory);
                        }
                        else
                        {
                            OpenDirectoryIndexer.Session.Parameters.Remove(Constants.Parameters_Password);
                            Console.WriteLine($"Error. Code: {indexResponse.Error.Code}, Message: {indexResponse.Error.Message}. Stopping.");
                            Logger.Error($"Error. Code: {indexResponse.Error.Code}, Message: {indexResponse.Error.Message}. Stopping.");
                        }
                    }
                }
                else
                {
                    webDirectory = await ScanIndexAsync(httpClient, webDirectory);
                }
            }
            catch (Exception ex)
            {
                RateLimiter.AddDelay(TimeSpan.FromSeconds(5));
                Logger.Error(ex, $"Error parsing {Parser} for URL: {webDirectory.Url}");
                webDirectory.Error = true;

                OpenDirectoryIndexer.Session.Errors++;

                if (!OpenDirectoryIndexer.Session.UrlsWithErrors.Contains(webDirectory.Url))
                {
                    OpenDirectoryIndexer.Session.UrlsWithErrors.Add(webDirectory.Url);
                }

                throw;
            }

            return webDirectory;
        }

        private static async Task<WebDirectory> ScanIndexAsync(HttpClient httpClient, WebDirectory webDirectory)
        {
            Logger.Debug($"Retrieving listings for {webDirectory.Uri} with password: {OpenDirectoryIndexer.Session.Parameters[Constants.Parameters_Password]}");

            webDirectory.Parser = Parser;

            try
            {
                if (!webDirectory.Url.EndsWith("/"))
                {
                    webDirectory.Url += "/";
                }

                int retry = 0;
                bool error = false;

                do
                {
                    long pageIndex = 0;
                    string nextPageToken = null;

                    do
                    {
                        await RateLimiter.RateLimit();

                        Logger.Warn($"Retrieving listings {(retry > 0 ? $"(retry {retry})" : string.Empty)} for {webDirectory.Uri}, page {pageIndex + 1}");

                        HttpResponseMessage httpResponseMessage = await httpClient.PostAsync(webDirectory.Uri, new StringContent(JsonConvert.SerializeObject(new Dictionary<string, object>
                        {
                            { "page_index", pageIndex },
                            { "page_token", nextPageToken },
                            { "password", OpenDirectoryIndexer.Session.Parameters[Constants.Parameters_Password] },
                            { "q", "" }
                        })));

                        webDirectory.ParsedSuccessfully = httpResponseMessage.IsSuccessStatusCode;
                        httpResponseMessage.EnsureSuccessStatusCode();

                        string responseJson = await httpResponseMessage.Content.ReadAsStringAsync();

                        Go2IndexResponse indexResponse = Go2IndexResponse.FromJson(responseJson);

                        webDirectory.ParsedSuccessfully = indexResponse.Error == null;

                        if (indexResponse.Error != null)
                        {
                            throw new Exception($"{indexResponse.Error.Code} | {indexResponse.Error.Message}");
                        }

                        if (indexResponse.Data?.Error != null)
                        {
                            if (indexResponse.Data.Error?.Message == "Rate Limit Exceeded")
                            {
                                error = true;
                                retry++;
                                RateLimiter.AddDelay(TimeSpan.FromSeconds(5));
                                Logger.Warn($"Error processing {Parser}, will be retried, for URL: {webDirectory.Url}");
                                continue;
                            }
                            else
                            {
                                throw new Exception($"{indexResponse.Data.Error?.Code} | {indexResponse.Data.Error?.Message}");
                            }
                        }

                        nextPageToken = indexResponse.NextPageToken;
                        pageIndex = indexResponse.CurPageIndex + 1;

                        foreach (File file in indexResponse.Data.Files)
                        {
                            if (file.MimeType == FolderMimeType)
                            {
                                webDirectory.Subdirectories.Add(new WebDirectory(webDirectory)
                                {
                                    Parser = Parser,
                                    // Yes, string concatenation, do not use new Uri(webDirectory.Uri, file.Name), because things could end with a space...
                                    Url = $"{webDirectory.Uri}{Uri.EscapeDataString(file.Name)}/",
                                    Name = file.Name
                                });
                            }
                            else
                            {
                                webDirectory.Files.Add(new WebFile
                                {
                                    Url = new Uri(webDirectory.Uri, Uri.EscapeDataString(file.Name)).ToString(),
                                    FileName = file.Name,
                                    FileSize = file.Size
                                });
                            }
                        }
                    } while (!string.IsNullOrWhiteSpace(nextPageToken));
                } while (error && retry < 5);

                webDirectory.Error = false;
            }
            catch (Exception ex)
            {
                RateLimiter.AddDelay(TimeSpan.FromSeconds(5));
                Logger.Error(ex, $"Error processing {Parser}, will be retried, for URL: {webDirectory.Url}");
                webDirectory.Error = true;

                OpenDirectoryIndexer.Session.Errors++;

                if (!OpenDirectoryIndexer.Session.UrlsWithErrors.Contains(webDirectory.Url))
                {
                    OpenDirectoryIndexer.Session.UrlsWithErrors.Add(webDirectory.Url);
                }

                //throw;
            }

            return webDirectory;
        }
    }
}
