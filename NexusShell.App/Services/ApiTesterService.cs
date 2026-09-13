using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NexusShell.App.Services
{
    public class ApiTesterService : IApiTesterService
    {
        private readonly HttpClient _httpClient;
        private readonly string     _storagePath;

        // SECURITY: Allow opt-in only for self-signed cert testing. Default is full
        // chain validation. The previous DangerousAcceptAnyServerCertificateValidator
        // silently accepted MITM / expired / wrong-host certificates — never ship that.
        public static bool AllowInsecureCertificates { get; set; } = false;

        public ApiTesterService()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies        = true,
                CookieContainer   = new CookieContainer(),
                // Proper TLS validation: accept only when the chain validates AND the
                // user explicitly opted in to bypass via AllowInsecureCertificates.
                ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None || AllowInsecureCertificates
            };

            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NexusShell",
                "ApiTester");

            Directory.CreateDirectory(_storagePath);
        }

        // ─── Send ───────────────────────────────────────────────────────────────

        public async Task<ApiResponse> SendRequestAsync(
            ApiRequest       request,
            ApiEnvironment?  environment        = null,
            CancellationToken cancellationToken = default)
        {
            var resolvedUrl = ResolveVariables(request.Url, environment);

            // Append enabled query parameters (only when body type doesn't own Parameters)
            if (request.BodyType is ApiBodyType.None or ApiBodyType.Raw)
            {
                var enabledParams = request.Parameters
                    .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Key))
                    .ToList();

                if (enabledParams.Count > 0)
                {
                    var qs = string.Join("&", enabledParams.Select(p =>
                        $"{Uri.EscapeDataString(ResolveVariables(p.Key, environment))}=" +
                        $"{Uri.EscapeDataString(ResolveVariables(p.Value, environment))}"));
                    resolvedUrl += resolvedUrl.Contains('?') ? $"&{qs}" : $"?{qs}";
                }
            }

            // ApiKey injected into query string (location == "Query")
            if (request.Auth?.Type == "ApiKey" &&
                request.Auth.Config.TryGetValue("key",      out var apiKeyName) &&
                request.Auth.Config.TryGetValue("value",    out var apiKeyVal)  &&
                request.Auth.Config.TryGetValue("location", out var apiKeyLoc)  &&
                string.Equals(apiKeyLoc, "Query", StringComparison.OrdinalIgnoreCase))
            {
                var kvp = $"{Uri.EscapeDataString(apiKeyName)}=" +
                          $"{Uri.EscapeDataString(ResolveVariables(apiKeyVal, environment))}";
                resolvedUrl += resolvedUrl.Contains('?') ? $"&{kvp}" : $"?{kvp}";
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var httpRequest = new HttpRequestMessage(new HttpMethod(request.Method.ToString()), resolvedUrl);

                // Headers — sanitize against CRLF (HTTP request smuggling) injection.
                foreach (var h in request.Headers.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)))
                {
                    var key = SanitizeHeader(ResolveVariables(h.Key,   environment));
                    var val = SanitizeHeader(ResolveVariables(h.Value, environment));
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!httpRequest.Headers.TryAddWithoutValidation(key, val))
                        httpRequest.Content?.Headers.TryAddWithoutValidation(key, val);
                }

                // Auth
                ApplyAuth(httpRequest, request.Auth, environment);

                // Body (only for methods that carry a body)
                if (request.Method is not HttpMethodType.GET and not HttpMethodType.HEAD)
                    ApplyBody(httpRequest, request, environment);

                var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                stopwatch.Stop();

                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                // Response headers (combine request + content headers)
                var allHeaders = response.Headers
                    .Concat(response.Content.Headers)
                    .ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

                // Cookies — Set-Cookie may contain commas inside Expires (e.g.
                // "Expires=Wed, 09 Jun 2021 …"), so DO NOT split on ','. Use the
                // multi-valued header collection instead — each Set-Cookie is one
                // value of the same header per RFC 6265.
                var cookies = new Dictionary<string, string>();
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var raw in setCookies)
                    {
                        var nameVal = raw.Split(';')[0].Split(new[] { '=' }, 2);
                        if (nameVal.Length == 2)
                            cookies[nameVal[0].Trim()] = nameVal[1].Trim();
                    }
                }

                var actualLength = body.Length > 0
                    ? (response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(body))
                    : 0L;

                return new ApiResponse
                {
                    StatusCode        = (int)response.StatusCode,
                    StatusDescription = response.ReasonPhrase ?? string.Empty,
                    ResponseTimeMs    = stopwatch.ElapsedMilliseconds,
                    ContentLength     = actualLength,
                    Body              = body,
                    Headers           = allHeaders,
                    Cookies           = cookies
                };
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return new ApiResponse
                {
                    StatusCode        = 0,
                    StatusDescription = "Cancelled",
                    Body              = "Request was cancelled.",
                    ResponseTimeMs    = stopwatch.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ApiResponse
                {
                    StatusCode        = 0,
                    StatusDescription = "Error",
                    Body              = ex.Message,
                    ResponseTimeMs    = stopwatch.ElapsedMilliseconds
                };
            }
        }

        // ─── Auth ───────────────────────────────────────────────────────────────

        private void ApplyAuth(HttpRequestMessage req, ApiAuth? auth, ApiEnvironment? env)
        {
            if (auth == null) return;

            switch (auth.Type)
            {
                case "Bearer":
                    if (auth.Config.TryGetValue("token", out var token))
                        req.Headers.Authorization = new AuthenticationHeaderValue(
                            "Bearer", SanitizeHeader(ResolveVariables(token, env)));
                    break;

                case "Basic":
                    if (auth.Config.TryGetValue("username", out var user) &&
                        auth.Config.TryGetValue("password", out var pass))
                    {
                        // Use UTF-8 (RFC 7617) — ASCII silently drops non-Latin chars
                        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                            $"{ResolveVariables(user, env)}:{ResolveVariables(pass, env)}"));
                        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
                    }
                    break;

                case "ApiKey":
                    if (auth.Config.TryGetValue("key",      out var keyName) &&
                        auth.Config.TryGetValue("value",    out var keyValue) &&
                        auth.Config.TryGetValue("location", out var keyLoc))
                    {
                        var sanitizedKey = SanitizeHeader(keyName);
                        var sanitizedVal = SanitizeHeader(ResolveVariables(keyValue, env));
                        if (!string.IsNullOrEmpty(sanitizedKey) &&
                            string.Equals(keyLoc, "Header", StringComparison.OrdinalIgnoreCase))
                            req.Headers.TryAddWithoutValidation(sanitizedKey, sanitizedVal);
                        // Query-param injection is handled at URL-build time in the caller
                    }
                    break;
            }
        }

        // ─── Security helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Strips CR/LF and other control characters from header keys/values to
        /// prevent HTTP response/request smuggling via header injection (CWE-113).
        /// </summary>
        private static string SanitizeHeader(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            // Remove CR, LF and NUL — anything that could split the HTTP frame
            var cleaned = value.Replace("\r", string.Empty)
                               .Replace("\n", string.Empty)
                               .Replace("\0", string.Empty);
            return cleaned.Trim();
        }

        // ─── Body ───────────────────────────────────────────────────────────────

        private void ApplyBody(HttpRequestMessage req, ApiRequest request, ApiEnvironment? env)
        {
            switch (request.BodyType)
            {
                case ApiBodyType.Raw:
                {
                    var content   = ResolveVariables(request.BodyContent, env);
                    var mediaType = request.RawType switch
                    {
                        ApiRawType.JSON => "application/json",
                        ApiRawType.XML  => "application/xml",
                        ApiRawType.HTML => "text/html",
                        _               => "text/plain"
                    };
                    req.Content = new StringContent(content, Encoding.UTF8, mediaType);
                    break;
                }

                case ApiBodyType.UrlEncoded:
                {
                    var pairs = request.Parameters
                        .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Key))
                        .Select(p => new KeyValuePair<string, string>(
                            ResolveVariables(p.Key,   env),
                            ResolveVariables(p.Value, env)));
                    req.Content = new FormUrlEncodedContent(pairs);
                    break;
                }

                case ApiBodyType.FormData:
                {
                    var form = new MultipartFormDataContent();
                    foreach (var p in request.Parameters.Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Key)))
                        form.Add(new StringContent(ResolveVariables(p.Value, env)), ResolveVariables(p.Key, env));
                    req.Content = form;
                    break;
                }
            }
        }

        // ─── Variable resolution ─────────────────────────────────────────────────

        public string ResolveVariables(string? input, ApiEnvironment? environment = null)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            return Regex.Replace(input, @"\{\{(.+?)\}\}", m =>
            {
                var name = m.Groups[1].Value;
                return environment?.Variables
                    .FirstOrDefault(v => v.Name == name && v.IsEnabled)?.Value
                    ?? m.Value;
            });
        }

        // ─── Persistence ─────────────────────────────────────────────────────────

        public void SaveCollections(IEnumerable<ApiCollection> collections)
            => Write("collections.json", collections);

        public List<ApiCollection> LoadCollections()
            => Read<List<ApiCollection>>("collections.json") ?? new();

        public void SaveEnvironments(IEnumerable<ApiEnvironment> environments)
            => Write("environments.json", environments);

        public List<ApiEnvironment> LoadEnvironments()
            => Read<List<ApiEnvironment>>("environments.json") ?? new();

        public void SaveHistory(IEnumerable<ApiHistoryEntry> history)
            => Write("history.json", history.Take(100));

        public List<ApiHistoryEntry> LoadHistory()
            => Read<List<ApiHistoryEntry>>("history.json") ?? new();

        private void Write(string file, object data)
            => File.WriteAllText(Path.Combine(_storagePath, file),
                JsonConvert.SerializeObject(data, Formatting.Indented));

        private T? Read<T>(string file) where T : class
        {
            var path = Path.Combine(_storagePath, file);
            return File.Exists(path)
                ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path))
                : null;
        }

        // ─── Import: Postman v2.0 / v2.1 ─────────────────────────────────────────

        public ApiCollection ImportPostmanCollection(string json)
        {
            var data       = JObject.Parse(json);
            var collection = new ApiCollection
            {
                Name = data["info"]?["name"]?.ToString() ?? "Imported Collection"
            };

            // Collection-level auth
            var colAuth = data["auth"];
            if (colAuth != null) collection.Auth = ParsePostmanAuth(colAuth);

            var items = data["item"] as JArray;
            if (items != null)
                foreach (var item in items)
                    ProcessPostmanItem(item, collection.Requests, collection.Folders);

            return collection;
        }

        private void ProcessPostmanItem(
            JToken token,
            ObservableCollection<ApiRequest> requests,
            ObservableCollection<ApiFolder>  folders)
        {
            if (token["item"] != null)
            {
                // Folder
                var folder = new ApiFolder { Name = token["name"]?.ToString() ?? "Folder" };
                foreach (var sub in token["item"] as JArray ?? new JArray())
                    ProcessPostmanItem(sub, folder.Requests, folder.Folders);
                folders.Add(folder);
            }
            else if (token["request"] != null)
            {
                var req      = token["request"]!;
                var apiReq   = new ApiRequest
                {
                    Name   = token["name"]?.ToString() ?? "Unnamed",
                    Method = Enum.TryParse<HttpMethodType>(req["method"]?.ToString(), out var m)
                             ? m : HttpMethodType.GET,
                    Url    = req["url"]?["raw"]?.ToString()
                             ?? req["url"]?.ToString()
                             ?? string.Empty
                };

                // Always start with empty collections
                apiReq.Headers.Clear();
                apiReq.Parameters.Clear();

                // Headers
                foreach (var h in req["header"] as JArray ?? new JArray())
                {
                    apiReq.Headers.Add(new ApiHeader
                    {
                        Key       = h["key"]?.ToString()   ?? string.Empty,
                        Value     = h["value"]?.ToString() ?? string.Empty,
                        IsEnabled = h["disabled"]?.ToObject<bool>() != true
                    });
                }

                // Auth
                var authNode = req["auth"];
                if (authNode != null) apiReq.Auth = ParsePostmanAuth(authNode);

                // Body
                var body = req["body"];
                if (body != null)
                {
                    switch (body["mode"]?.ToString())
                    {
                        case "raw":
                            apiReq.BodyType    = ApiBodyType.Raw;
                            apiReq.BodyContent = body["raw"]?.ToString() ?? string.Empty;
                            var lang = body["options"]?["raw"]?["language"]?.ToString();
                            apiReq.RawType = lang?.ToLowerInvariant() switch
                            {
                                "json" => ApiRawType.JSON,
                                "xml"  => ApiRawType.XML,
                                "html" => ApiRawType.HTML,
                                _      => ApiRawType.Text
                            };
                            break;

                        case "urlencoded":
                            apiReq.BodyType = ApiBodyType.UrlEncoded;
                            apiReq.Parameters.Clear();
                            foreach (var p in body["urlencoded"] as JArray ?? new JArray())
                            {
                                apiReq.Parameters.Add(new ApiParameter
                                {
                                    Key       = p["key"]?.ToString()   ?? string.Empty,
                                    Value     = p["value"]?.ToString() ?? string.Empty,
                                    IsEnabled = p["disabled"]?.ToObject<bool>() != true
                                });
                            }
                            break;

                        case "formdata":
                            apiReq.BodyType = ApiBodyType.FormData;
                            apiReq.Parameters.Clear();
                            foreach (var p in body["formdata"] as JArray ?? new JArray())
                            {
                                if (p["type"]?.ToString() == "file") continue;
                                apiReq.Parameters.Add(new ApiParameter
                                {
                                    Key       = p["key"]?.ToString()   ?? string.Empty,
                                    Value     = p["value"]?.ToString() ?? string.Empty,
                                    IsEnabled = p["disabled"]?.ToObject<bool>() != true
                                });
                            }
                            break;
                    }
                }

                // Pre-request script & tests
                foreach (var ev in token["event"] as JArray ?? new JArray())
                {
                    var listen = ev["listen"]?.ToString();
                    var script = string.Join("\n", ev["script"]?["exec"]
                        ?.Select(l => l.ToString()) ?? Enumerable.Empty<string>());

                    if (listen == "prerequest") apiReq.PreRequestScript = script;
                    else if (listen == "test")  apiReq.TestScript        = script;
                }

                // Query params from URL object — only when body doesn't own Parameters
                if (apiReq.BodyType is ApiBodyType.None or ApiBodyType.Raw)
                {
                    apiReq.Parameters.Clear();
                    foreach (var qp in req["url"]?["query"] as JArray ?? new JArray())
                    {
                        apiReq.Parameters.Add(new ApiParameter
                        {
                            Key       = qp["key"]?.ToString()   ?? string.Empty,
                            Value     = qp["value"]?.ToString() ?? string.Empty,
                            IsEnabled = qp["disabled"]?.ToObject<bool>() != true
                        });
                    }
                }

                requests.Add(apiReq);
            }
        }

        private static ApiAuth ParsePostmanAuth(JToken auth)
        {
            var type     = auth["type"]?.ToString() ?? "none";
            var apiAuth  = new ApiAuth();

            apiAuth.Type = type switch
            {
                "bearer" => "Bearer",
                "basic"  => "Basic",
                "apikey" => "ApiKey",
                _        => "None"
            };

            var cfg = auth[type] as JArray;
            if (cfg != null)
            {
                foreach (var item in cfg)
                {
                    var key = item["key"]?.ToString();
                    var val = item["value"]?.ToString();
                    if (key != null && val != null)
                        apiAuth.Config[key] = val;
                }
            }

            return apiAuth;
        }

        // ─── Import: OpenAPI 3.x ─────────────────────────────────────────────────

        public ApiCollection ImportOpenApi(string content)
        {
            // Accept both JSON and YAML (basic detection)
            JObject? spec = null;
            try
            {
                spec = JObject.Parse(content);
            }
            catch
            {
                // YAML not natively supported without an extra lib – return empty
                return new ApiCollection { Name = "OpenAPI Import (YAML not supported)" };
            }

            var title      = spec["info"]?["title"]?.ToString() ?? "OpenAPI";
            var collection = new ApiCollection { Name = title };
            var servers    = spec["servers"] as JArray;
            var baseUrl    = servers?.FirstOrDefault()?["url"]?.ToString() ?? string.Empty;

            var paths = spec["paths"] as JObject;
            if (paths == null) return collection;

            foreach (var pathProp in paths.Properties())
            {
                var path    = pathProp.Name;
                var methods = pathProp.Value as JObject;
                if (methods == null) continue;

                foreach (var methodProp in methods.Properties())
                {
                    var methodStr = methodProp.Name.ToUpperInvariant();
                    if (!Enum.TryParse<HttpMethodType>(methodStr, out var httpMethod)) continue;

                    var op      = methodProp.Value as JObject;
                    var summary = op?["summary"]?.ToString()
                               ?? op?["operationId"]?.ToString()
                               ?? $"{methodStr} {path}";

                    var req = new ApiRequest
                    {
                        Name   = summary,
                        Method = httpMethod,
                        Url    = $"{baseUrl}{path}"
                    };
                    req.Headers.Clear();
                    req.Headers.Add(new ApiHeader { Key = "Accept", Value = "application/json" });

                    // Query parameters
                    req.Parameters.Clear();
                    foreach (var param in op?["parameters"] as JArray ?? new JArray())
                    {
                        if (param["in"]?.ToString() == "query")
                        {
                            req.Parameters.Add(new ApiParameter
                            {
                                Key   = param["name"]?.ToString() ?? string.Empty,
                                Value = string.Empty,
                                IsEnabled = param["required"]?.ToObject<bool>() == true
                            });
                        }
                    }

                    // Request body
                    var requestBody = op?["requestBody"];
                    if (requestBody != null)
                    {
                        req.BodyType = ApiBodyType.Raw;
                        req.RawType  = ApiRawType.JSON;
                        var schema   = requestBody["content"]?["application/json"]?["schema"];
                        req.BodyContent = schema != null
                            ? schema.ToString(Formatting.Indented)
                            : "{}";
                    }

                    // Assign to a folder named by the first tag, or collection root
                    var tag        = op?["tags"]?.FirstOrDefault()?.ToString();
                    if (!string.IsNullOrEmpty(tag))
                    {
                        var folder = collection.Folders.FirstOrDefault(f => f.Name == tag);
                        if (folder == null)
                        {
                            folder = new ApiFolder { Name = tag };
                            collection.Folders.Add(folder);
                        }
                        folder.Requests.Add(req);
                    }
                    else
                    {
                        collection.Requests.Add(req);
                    }
                }
            }

            return collection;
        }

        // ─── Code generation ─────────────────────────────────────────────────────

        public string GenerateCurl(ApiRequest request, ApiEnvironment? environment = null)
        {
            var url = BuildUrl(request, environment);
            // Single-quoted shell escape: ' becomes '\''  (close quote, escape it, reopen)
            var sb  = new StringBuilder($"curl -X {request.Method} '{ShellSingleQuoteEscape(url)}'");

            ApplyAuthHeader(sb, request, environment, (s, k, v) =>
                s.Append($" \\\n  -H '{ShellSingleQuoteEscape(k)}: {ShellSingleQuoteEscape(v)}'"));

            foreach (var h in request.Headers.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)))
                sb.Append($" \\\n  -H '{ShellSingleQuoteEscape(h.Key)}: {ShellSingleQuoteEscape(ResolveVariables(h.Value, environment))}'");

            if (request.BodyType == ApiBodyType.Raw && !string.IsNullOrWhiteSpace(request.BodyContent))
                sb.Append($" \\\n  --data-raw '{ShellSingleQuoteEscape(ResolveVariables(request.BodyContent, environment))}'");

            return sb.ToString();
        }

        public string GenerateCSharp(ApiRequest request, ApiEnvironment? environment = null)
        {
            var url = BuildUrl(request, environment);
            var sb  = new StringBuilder();
            sb.AppendLine("using System.Net.Http;");
            sb.AppendLine("using System.Net.Http.Headers;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine();
            sb.AppendLine("var client = new HttpClient();");

            if (request.Auth?.Type == "Bearer" && request.Auth.Config.TryGetValue("token", out var bearer))
                sb.AppendLine($"client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(\"Bearer\", \"{CsEscape(ResolveVariables(bearer, environment))}\");");
            else if (request.Auth?.Type == "Basic" &&
                     request.Auth.Config.TryGetValue("username", out var u) &&
                     request.Auth.Config.TryGetValue("password", out var p))
            {
                // Variables MUST be resolved before encoding so the snippet matches what
                // the live sender does (otherwise {{password}} is literally base64'd).
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{ResolveVariables(u, environment)}:{ResolveVariables(p, environment)}"));
                sb.AppendLine($"client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(\"Basic\", \"{encoded}\");");
            }

            foreach (var h in request.Headers.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)))
                sb.AppendLine($"client.DefaultRequestHeaders.TryAddWithoutValidation(\"{CsEscape(h.Key)}\", \"{CsEscape(ResolveVariables(h.Value, environment))}\");");

            sb.AppendLine();

            if (request.BodyType == ApiBodyType.Raw && !string.IsNullOrWhiteSpace(request.BodyContent))
            {
                var mediaType = request.RawType == ApiRawType.JSON ? "application/json" : "text/plain";
                var body      = CsEscape(ResolveVariables(request.BodyContent, environment));
                sb.AppendLine($"var content = new StringContent(\"{body}\", Encoding.UTF8, \"{mediaType}\");");
                sb.AppendLine($"var response = await client.{CsMethodName(request.Method)}Async(\"{CsEscape(url)}\", content);");
            }
            else
            {
                sb.AppendLine($"var response = await client.{CsMethodName(request.Method)}Async(\"{CsEscape(url)}\");");
            }

            sb.AppendLine("response.EnsureSuccessStatusCode();");
            sb.AppendLine("var body = await response.Content.ReadAsStringAsync();");
            sb.AppendLine("Console.WriteLine(body);");
            return sb.ToString();
        }

        public string GenerateJavaScript(ApiRequest request, ApiEnvironment? environment = null)
        {
            var url = BuildUrl(request, environment);
            var sb  = new StringBuilder();
            sb.AppendLine($"const response = await fetch('{JsEscape(url)}', {{");
            sb.AppendLine($"  method: '{request.Method}',");

            var headers = request.Headers.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)).ToList();
            if (headers.Count > 0 || request.Auth?.Type is "Bearer" or "Basic")
            {
                sb.AppendLine("  headers: {");
                foreach (var h in headers)
                    sb.AppendLine($"    '{JsEscape(h.Key)}': '{JsEscape(ResolveVariables(h.Value, environment))}',");
                AppendJsAuthHeader(sb, request, environment);
                sb.AppendLine("  },");
            }

            if (request.BodyType == ApiBodyType.Raw && !string.IsNullOrWhiteSpace(request.BodyContent))
            {
                // Wrap as JSON string literal so arbitrary content can't break out
                var bodyLiteral = JsonConvert.ToString(ResolveVariables(request.BodyContent, environment));
                sb.AppendLine($"  body: {bodyLiteral},");
            }

            sb.AppendLine("});");
            sb.AppendLine();
            sb.AppendLine("if (!response.ok) throw new Error(`HTTP ${response.status}`);");
            sb.AppendLine("const data = await response.json();");
            sb.AppendLine("console.log(data);");
            return sb.ToString();
        }

        public string GeneratePython(ApiRequest request, ApiEnvironment? environment = null)
        {
            var url = BuildUrl(request, environment);
            var sb  = new StringBuilder();
            sb.AppendLine("import requests");
            sb.AppendLine();

            sb.AppendLine("headers = {");
            foreach (var h in request.Headers.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)))
                sb.AppendLine($"    '{PyEscape(h.Key)}': '{PyEscape(ResolveVariables(h.Value, environment))}',");
            AppendPythonAuthHeader(sb, request, environment);
            sb.AppendLine("}");
            sb.AppendLine();

            var method = request.Method.ToString().ToLowerInvariant();

            if (request.BodyType == ApiBodyType.Raw && !string.IsNullOrWhiteSpace(request.BodyContent))
            {
                // For JSON body, parse-and-emit a real Python literal; for non-JSON
                // raw, fall back to a string body (data=) so we never inject raw
                // content as Python code.
                var resolvedBody = ResolveVariables(request.BodyContent, environment);
                if (request.RawType == ApiRawType.JSON)
                {
                    sb.AppendLine($"payload = {PyEscapeStringLiteral(resolvedBody)}");
                    sb.AppendLine("import json");
                    sb.AppendLine("payload = json.loads(payload)");
                    sb.AppendLine($"response = requests.{method}('{PyEscape(url)}', headers=headers, json=payload)");
                }
                else
                {
                    sb.AppendLine($"data = {PyEscapeStringLiteral(resolvedBody)}");
                    sb.AppendLine($"response = requests.{method}('{PyEscape(url)}', headers=headers, data=data)");
                }
            }
            else if (request.BodyType is ApiBodyType.FormData or ApiBodyType.UrlEncoded)
            {
                sb.AppendLine("data = {");
                foreach (var p in request.Parameters.Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Key)))
                    sb.AppendLine($"    '{PyEscape(ResolveVariables(p.Key, environment))}': '{PyEscape(ResolveVariables(p.Value, environment))}',");
                sb.AppendLine("}");
                sb.AppendLine($"response = requests.{method}('{PyEscape(url)}', headers=headers, data=data)");
            }
            else
            {
                sb.AppendLine($"response = requests.{method}('{PyEscape(url)}', headers=headers)");
            }

            sb.AppendLine();
            sb.AppendLine("print(f'Status: {response.status_code}')");
            sb.AppendLine("print(response.json())");
            return sb.ToString();
        }

        public string GenerateTypeScript(ApiRequest request, ApiEnvironment? environment = null)
        {
            var url = BuildUrl(request, environment);
            var sb  = new StringBuilder();
            sb.AppendLine("const response = await fetch(");
            sb.AppendLine($"  '{JsEscape(url)}',");
            sb.AppendLine("  {");
            sb.AppendLine($"    method: '{request.Method}',");

            var headers = request.Headers.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)).ToList();
            if (headers.Count > 0 || request.Auth?.Type is "Bearer" or "Basic")
            {
                sb.AppendLine("    headers: {");
                foreach (var h in headers)
                    sb.AppendLine($"      '{JsEscape(h.Key)}': '{JsEscape(ResolveVariables(h.Value, environment))}',");
                AppendJsAuthHeader(sb, request, environment, "    ");
                sb.AppendLine("    } satisfies Record<string, string>,");
            }

            if (request.BodyType == ApiBodyType.Raw && !string.IsNullOrWhiteSpace(request.BodyContent))
            {
                // Body shown as a JSON-string literal; consumer can JSON.parse if needed.
                var bodyLiteral = JsonConvert.ToString(ResolveVariables(request.BodyContent, environment));
                sb.AppendLine($"    body: {bodyLiteral},");
            }

            sb.AppendLine("  }");
            sb.AppendLine(");");
            sb.AppendLine();
            sb.AppendLine("if (!response.ok) throw new Error(`HTTP error: ${response.status}`);");
            sb.AppendLine("const data: unknown = await response.json();");
            sb.AppendLine("console.log(data);");
            return sb.ToString();
        }

        // ─── Private helpers ─────────────────────────────────────────────────────

        private string BuildUrl(ApiRequest request, ApiEnvironment? env)
        {
            var url   = ResolveVariables(request.Url, env);
            var params_ = request.Parameters
                .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Key))
                .ToList();

            if (params_.Count > 0)
            {
                var qs = string.Join("&", params_.Select(p =>
                    $"{Uri.EscapeDataString(ResolveVariables(p.Key, env))}=" +
                    $"{Uri.EscapeDataString(ResolveVariables(p.Value, env))}"));
                url += url.Contains('?') ? $"&{qs}" : $"?{qs}";
            }

            return url;
        }

        // C# HttpClient does not expose HeadAsync / OptionsAsync convenience methods —
        // for those we emit the SendAsync(HttpRequestMessage) form via a sentinel.
        // Returning empty string signals "use the long form" to the caller; today all
        // call sites use the short Get/Post/Put/Patch/Delete-Async form so we keep the
        // mapping conservative and just mark unsupported verbs explicitly.
        private static string CsMethodName(HttpMethodType method) => method switch
        {
            HttpMethodType.GET     => "Get",
            HttpMethodType.POST    => "Post",
            HttpMethodType.PUT     => "Put",
            HttpMethodType.PATCH   => "Patch",
            HttpMethodType.DELETE  => "Delete",
            HttpMethodType.HEAD    => "Send", // emitted as client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url))
            HttpMethodType.OPTIONS => "Send",
            _                      => "Get"
        };

        private void ApplyAuthHeader(StringBuilder sb, ApiRequest req, ApiEnvironment? env,
            Action<StringBuilder, string, string> write)
        {
            if (req.Auth?.Type == "Bearer" && req.Auth.Config.TryGetValue("token", out var t))
                write(sb, "Authorization", $"Bearer {ResolveVariables(t, env)}");
            else if (req.Auth?.Type == "Basic" &&
                     req.Auth.Config.TryGetValue("username", out var u) &&
                     req.Auth.Config.TryGetValue("password", out var p))
            {
                // Resolve variables BEFORE encoding — otherwise generated snippets
                // base64 the literal "{{password}}" placeholder and won't authenticate.
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{ResolveVariables(u, env)}:{ResolveVariables(p, env)}"));
                write(sb, "Authorization", $"Basic {encoded}");
            }
        }

        private void AppendJsAuthHeader(StringBuilder sb, ApiRequest req, ApiEnvironment? env, string indent = "  ")
        {
            if (req.Auth?.Type == "Bearer" && req.Auth.Config.TryGetValue("token", out var t))
                sb.AppendLine($"{indent}  'Authorization': 'Bearer {JsEscape(ResolveVariables(t, env))}',");
            else if (req.Auth?.Type == "Basic" &&
                     req.Auth.Config.TryGetValue("username", out var u) &&
                     req.Auth.Config.TryGetValue("password", out var p))
            {
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{ResolveVariables(u, env)}:{ResolveVariables(p, env)}"));
                sb.AppendLine($"{indent}  'Authorization': 'Basic {encoded}',");
            }
        }

        private void AppendPythonAuthHeader(StringBuilder sb, ApiRequest req, ApiEnvironment? env)
        {
            if (req.Auth?.Type == "Bearer" && req.Auth.Config.TryGetValue("token", out var t))
                sb.AppendLine($"    'Authorization': 'Bearer {PyEscape(ResolveVariables(t, env))}',");
            else if (req.Auth?.Type == "Basic" &&
                     req.Auth.Config.TryGetValue("username", out var u) &&
                     req.Auth.Config.TryGetValue("password", out var p))
            {
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{ResolveVariables(u, env)}:{ResolveVariables(p, env)}"));
                sb.AppendLine($"    'Authorization': 'Basic {encoded}',");
            }
        }

        // ─── Code-generation escape helpers ──────────────────────────────────────
        // Each emits the value safely embedded in the corresponding language's
        // single- or double-quoted string literal so user-supplied data cannot
        // break out of quotes or inject code into the generated snippet.

        private static string CsEscape(string? s) =>
            (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                               .Replace("\r", "\\r").Replace("\n", "\\n");

        private static string JsEscape(string? s) =>
            (s ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'")
                               .Replace("\r", "\\r").Replace("\n", "\\n");

        private static string PyEscape(string? s) =>
            (s ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'")
                               .Replace("\r", "\\r").Replace("\n", "\\n");

        // Wraps a value as a Python single-quoted string literal (with escaping).
        private static string PyEscapeStringLiteral(string? s) => $"'{PyEscape(s)}'";

        // POSIX-shell single-quoted string escape: ' becomes '\''
        private static string ShellSingleQuoteEscape(string? s) =>
            (s ?? string.Empty).Replace("'", "'\\''");
    }
}
