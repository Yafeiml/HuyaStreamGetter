using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace HuyaStreamGetter
{
    public abstract class BaseExtractor
    {
        protected readonly HttpClient _httpClient;
        protected readonly string _cookies;

        protected BaseExtractor(string? cookies)
        {
            _cookies = cookies ?? "";
            var handler = new HttpClientHandler
            {
                UseCookies = false
            };
            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        public abstract Task<string?> GetStreamUrlAsync(string url, string quality);

        protected long GetTimestampUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        protected long GetTimestampUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        protected string GetMd5(string input)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        
        protected string Base64Decode(string base64)
        {
            try {
                int mod4 = base64.Length % 4;
                if (mod4 > 0) base64 += new string('=', 4 - mod4);
                var bytes = Convert.FromBase64String(base64);
                return Encoding.UTF8.GetString(bytes);
            } catch {
                return "";
            }
        }
    }

    public class BilibiliExtractor : BaseExtractor
    {
        public BilibiliExtractor(string? cookies) : base(cookies) { }

        public override async Task<string?> GetStreamUrlAsync(string url, string quality)
        {
            try
            {
                string roomId = url.Split('?')[0].TrimEnd('/').Split('/').Last();
                if (string.IsNullOrEmpty(roomId)) return null;

                string qn = quality switch
                {
                    "OD" => "10000",
                    "BD" => "400",
                    "UHD" => "250",
                    "HD" => "150",
                    "SD" => "80",
                    "LD" => "80",
                    _ => "10000"
                };

                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/room/v1/Room/playUrl?cid={roomId}&qn={qn}&platform=web");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");
                if (!string.IsNullOrEmpty(_cookies)) request.Headers.Add("Cookie", _cookies);

                var response = await _httpClient.SendAsync(request);
                string jsonStr = await response.Content.ReadAsStringAsync();
                var json = JsonNode.Parse(jsonStr);
                
                if (json?["code"]?.GetValue<int>() == 0)
                {
                    var durlArray = json["data"]?["durl"]?.AsArray();
                    if (durlArray != null && durlArray.Count > 0)
                    {
                        foreach (var item in durlArray)
                        {
                            string durl = item?["url"]?.GetValue<string>() ?? "";
                            if (durl.Contains("d1--cn-gotcha")) return durl;
                        }
                        return durlArray.Last()?["url"]?.GetValue<string>();
                    }
                }
                else
                {
                    var v2Req = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id={roomId}&protocol=0,1&format=0,1,2&codec=0,1,2&qn={qn}&platform=web&ptype=8&dolby=5&panorama=1&hdr_type=0,1");
                    v2Req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    if (!string.IsNullOrEmpty(_cookies)) v2Req.Headers.Add("Cookie", _cookies);
                    
                    var v2Resp = await _httpClient.SendAsync(v2Req);
                    var v2JsonStr = await v2Resp.Content.ReadAsStringAsync();
                    var v2Json = JsonNode.Parse(v2JsonStr);
                    
                    if (v2Json?["data"]?["live_status"]?.GetValue<int>() == 0)
                        throw new Exception("Failed to get any stream (Not Live)");

                    var streamArray = v2Json?["data"]?["playurl_info"]?["playurl"]?["stream"]?.AsArray();
                    if (streamArray != null && streamArray.Count > 0)
                    {
                        var formatArray = streamArray[0]?["format"]?.AsArray();
                        var codecArray = formatArray?[0]?["codec"]?.AsArray();
                        if (codecArray != null && codecArray.Count > 0)
                        {
                            var firstCodec = codecArray[0];
                            string baseUrl = firstCodec?["base_url"]?.GetValue<string>() ?? "";
                            var urlInfoArray = firstCodec?["url_info"]?.AsArray();
                            if (urlInfoArray != null && urlInfoArray.Count > 0)
                            {
                                string host = urlInfoArray[0]?["host"]?.GetValue<string>() ?? "";
                                string extra = urlInfoArray[0]?["extra"]?.GetValue<string>() ?? "";
                                return host + baseUrl + extra;
                            }
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                throw new Exception($"Bilibili Error: {ex.Message}");
            }
        }
    }

    public class DouyuExtractor : BaseExtractor
    {
        public DouyuExtractor(string? cookies) : base(cookies) { }

        public override async Task<string?> GetStreamUrlAsync(string url, string quality)
        {
            try
            {
                string roomId = "";
                var match = Regex.Match(url, @"douyu\.com/(\d+)");
                if (match.Success) roomId = match.Groups[1].Value;
                else
                {
                    match = Regex.Match(url, @"rid=(\d+)");
                    if (match.Success) roomId = match.Groups[1].Value;
                }
                
                if (string.IsNullOrEmpty(roomId))
                {
                    string path = url.Split(new[] { "douyu.com/" }, StringSplitOptions.None)[1].Split('?')[0].Split('/')[0];
                    var req = new HttpRequestMessage(HttpMethod.Get, $"https://m.douyu.com/{path}");
                    req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36");
                    var res = await _httpClient.SendAsync(req);
                    var html = await res.Content.ReadAsStringAsync();
                    var m = Regex.Match(html, @"""rid"":(\d+)");
                    if (m.Success) roomId = m.Groups[1].Value;
                }

                if (string.IsNullOrEmpty(roomId)) return null;

                string rate = quality switch
                {
                    "OD" => "0",
                    "BD" => "0",
                    "UHD" => "3",
                    "HD" => "2",
                    "SD" => "1",
                    "LD" => "1",
                    _ => "0"
                };

                // Get encryption params
                var encReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.douyu.com/wgapi/livenc/liveweb/websec/getEncryption?did=10000000000000000000000000001501");
                encReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36");
                var encRes = await _httpClient.SendAsync(encReq);
                var encJson = JsonNode.Parse(await encRes.Content.ReadAsStringAsync());
                
                if (encJson?["error"]?.GetValue<int>() != 0)
                    throw new Exception("Douyu encryption fetch failed.");

                var white = encJson["data"];
                long ts = GetTimestampUnix();
                string secret = white?["rand_str"]?.GetValue<string>() ?? "";
                string salt = (white?["is_special"]?.GetValue<bool>() == false) ? $"{roomId}{ts}" : "";
                int encTime = white?["enc_time"]?.GetValue<int>() ?? 0;
                string key = white?["key"]?.GetValue<string>() ?? "";
                
                for (int i = 0; i < encTime; i++)
                {
                    secret = GetMd5(secret + key);
                }
                string auth = GetMd5(secret + key + salt);

                var paramDict = new Dictionary<string, string>
                {
                    { "rate", rate },
                    { "ver", "219032101" },
                    { "iar", "0" },
                    { "ive", "0" },
                    { "rid", roomId },
                    { "hevc", "0" },
                    { "fa", "0" },
                    { "sov", "0" },
                    { "enc_data", white?["enc_data"]?.GetValue<string>() ?? "" },
                    { "tt", ts.ToString() },
                    { "did", "10000000000000000000000000001501" },
                    { "auth", auth }
                };

                var playReq = new HttpRequestMessage(HttpMethod.Post, $"https://playweb.douyucdn.cn/lapi/live/getH5PlayV1/{roomId}");
                playReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36");
                playReq.Headers.Add("Origin", "https://www.douyu.com");
                playReq.Content = new FormUrlEncodedContent(paramDict);

                var playRes = await _httpClient.SendAsync(playReq);
                var playJson = JsonNode.Parse(await playRes.Content.ReadAsStringAsync());
                
                if (playJson?["error"]?.GetValue<int>() == 0)
                {
                    var data = playJson["data"];
                    string rtmpUrl = data?["rtmp_url"]?.GetValue<string>() ?? "";
                    string rtmpLive = data?["rtmp_live"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(rtmpUrl) && !string.IsNullOrEmpty(rtmpLive))
                    {
                        return $"{rtmpUrl}/{rtmpLive}";
                    }
                }
                throw new Exception("Failed to get any stream (Not Live)");
            }
            catch (Exception ex)
            {
                throw new Exception($"Douyu Error: {ex.Message}");
            }
        }
    }

    public class HuyaExtractor : BaseExtractor
    {
        private string? _sHlsUrl;
        private string? _sStreamName;
        private string? _sHlsUrlSuffix;
        private string? _sHlsAntiCode;

        private readonly long _uid;
        private readonly long _initUuid;
        private readonly long _sdkSid;

        public HuyaExtractor(string? cookies) : base(cookies) 
        { 
            long t13 = GetTimestampUnixMs();
            _sdkSid = t13;
            _initUuid = (long)((t13 % 10000000000L * 1000) + (1000 * new Random().NextDouble())) % 4294967295L;
            _uid = new Random().NextInt64(1400000000000L, 1400010000000L);
        }

        public string GetFreshUrl()
        {
            if (string.IsNullOrEmpty(_sHlsUrl) || string.IsNullOrEmpty(_sStreamName))
                return "";
            string newAntiCode = GetAntiCode(_sHlsAntiCode!, _sStreamName!);
            return $"{_sHlsUrl}/{_sStreamName}.{_sHlsUrlSuffix}?{newAntiCode}&ratio=";
        }

        private Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>();
            var parts = query.TrimStart('?').Split('&');
            foreach (var p in parts)
            {
                var kv = p.Split('=');
                if (kv.Length == 2) dict[kv[0]] = Uri.UnescapeDataString(kv[1]);
                else if (kv.Length == 1) dict[kv[0]] = "";
            }
            return dict;
        }

        private string GetAntiCode(string oldAntiCode, string streamName)
        {
            var query = ParseQueryString(oldAntiCode);
            
            long t13 = GetTimestampUnixMs();
            long seqId = _uid + _sdkSid;
            long targetUnixTime = (t13 + 110624) / 1000;
            string wsTime = targetUnixTime.ToString("x").ToLower();
            
            string fm = query.ContainsKey("fm") ? query["fm"] : "";
            string decodedFm = Base64Decode(fm);
            string wsSecretPf = decodedFm.Split('_')[0];
            
            string ctype = query.ContainsKey("ctype") ? query["ctype"] : "";
            string fs = query.ContainsKey("fs") ? query["fs"] : "";
            
            string wsSecretHash = GetMd5($"{seqId}|{ctype}|100");
            string wsSecret = $"{wsSecretPf}_{_uid}_{streamName}_{wsSecretHash}_{wsTime}";
            string wsSecretMd5 = GetMd5(wsSecret);
            
            return $"wsSecret={wsSecretMd5}&wsTime={wsTime}&seqid={seqId}&ctype={ctype}&ver=1&fs={fs}&uuid={_initUuid}&u={_uid}&t=100&sv=2403051612&sdk_sid={_sdkSid}&codec=264";
        }

        public override async Task<string?> GetStreamUrlAsync(string url, string quality)
        {
            try
            {
                string roomId = url.Split('?')[0].TrimEnd('/').Split('/').Last();
                if (roomId.Any(char.IsLetter))
                {
                    var mReq = new HttpRequestMessage(HttpMethod.Get, url);
                    mReq.Headers.Add("User-Agent", "ios/7.830 (ios 17.0; ; iPhone 15)");
                    var mRes = await _httpClient.SendAsync(mReq);
                    var html = await mRes.Content.ReadAsStringAsync();
                    var match = Regex.Match(html, @"ProfileRoom"":(.*?),""sPrivateHost");
                    if (match.Success) roomId = match.Groups[1].Value;
                }

                var req = new HttpRequestMessage(HttpMethod.Get, $"https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid={roomId}&showSecret=1");
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0");
                var res = await _httpClient.SendAsync(req);
                var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());

                string status = json?["data"]?["realLiveStatus"]?.GetValue<string>() ?? "";
                if (status != "ON") throw new Exception("Failed to get any stream (Not Live)");

                string liveType = json?["data"]?["liveData"]?["gameHostName"]?.GetValue<string>() ?? "";
                if (liveType == "lol")
                {
                    // LOL 分区走 PC 页面解析
                    var pcReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.huya.com/{roomId}");
                    pcReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0");
                    var pcRes = await _httpClient.SendAsync(pcReq);
                    var pcHtml = await pcRes.Content.ReadAsStringAsync();
                    var pcMatch = Regex.Match(pcHtml, @"stream: (\{""data"".*?),""iWebDefaultBitRate""");
                    if (pcMatch.Success)
                    {
                        var pcJsonStr = pcMatch.Groups[1].Value + "}";
                        var pcJson = JsonNode.Parse(pcJsonStr);
                        var streamInfo = pcJson?["data"]?[0]?["gameStreamInfoList"]?[0];
                        if (streamInfo != null)
                        {
                            _sStreamName = streamInfo["sStreamName"]?.GetValue<string>() ?? "";
                            _sHlsUrl = streamInfo["sHlsUrl"]?.GetValue<string>() ?? "";
                            _sHlsUrlSuffix = streamInfo["sHlsUrlSuffix"]?.GetValue<string>() ?? "m3u8";
                            _sHlsAntiCode = streamInfo["sHlsAntiCode"]?.GetValue<string>() ?? "";
                            string newAntiCode = GetAntiCode(_sHlsAntiCode, _sStreamName);
                            return $"{_sHlsUrl}/{_sStreamName}.{_sHlsUrlSuffix}?{newAntiCode}&ratio=";
                        }
                    }
                }

                var baseSteamInfoList = json?["data"]?["stream"]?["baseSteamInfoList"]?.AsArray();
                if (baseSteamInfoList != null && baseSteamInfoList.Count > 0)
                {
                    // 优先选 TX CDN
                    var txItem = baseSteamInfoList.FirstOrDefault(x => x?["sCdnType"]?.GetValue<string>() == "TX") ?? baseSteamInfoList[0];
                    if (txItem != null)
                    {
                        _sStreamName = txItem["sStreamName"]?.GetValue<string>() ?? "";
                        _sHlsUrl = txItem["sHlsUrl"]?.GetValue<string>() ?? "";
                        _sHlsUrlSuffix = txItem["sHlsUrlSuffix"]?.GetValue<string>() ?? "m3u8";
                        _sHlsAntiCode = txItem["sHlsAntiCode"]?.GetValue<string>() ?? "";
                        string newAntiCode = GetAntiCode(_sHlsAntiCode, _sStreamName);
                        string m3u8Url = $"{_sHlsUrl}/{_sStreamName}.{_sHlsUrlSuffix}?{newAntiCode}&ratio=";
                        return m3u8Url;
                    }
                }
                
                throw new Exception("Failed to get any stream (Not Live)");
            }
            catch (Exception ex)
            {
                throw new Exception($"Huya Error: {ex.Message}");
            }
        }
    }
}
