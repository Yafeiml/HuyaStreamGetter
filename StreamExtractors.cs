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
                string rawId = url.Split('?')[0].TrimEnd('/').Split('/').Last();
                if (string.IsNullOrEmpty(rawId)) return null;

                // 1. 获取真实房间号与开播状态
                string realRoomId = rawId;
                try
                {
                    var initReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/room/v1/Room/room_init?id={rawId}");
                    initReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36");
                    if (!string.IsNullOrEmpty(_cookies)) initReq.Headers.Add("Cookie", _cookies);
                    
                    var initRes = await _httpClient.SendAsync(initReq);
                    if (initRes.IsSuccessStatusCode)
                    {
                        var initJson = JsonNode.Parse(await initRes.Content.ReadAsStringAsync());
                        if (initJson?["code"]?.GetValue<int>() == 0)
                        {
                            int liveStatus = initJson["data"]?["live_status"]?.GetValue<int>() ?? 0;
                            if (liveStatus == 0)
                            {
                                throw new Exception("Not Live (未开播)");
                            }
                            realRoomId = initJson["data"]?["room_id"]?.ToString() ?? rawId;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("Not Live")) throw;
                }

                // 2. 映射画质 QN (OD/BD 均请求 10000 原画 1080P60)
                string qn = quality switch
                {
                    "OD" => "10000",
                    "BD" => "10000",
                    "UHD" => "400",
                    "HD" => "400",
                    "SD" => "250",
                    "LD" => "150",
                    _ => "10000"
                };

                // 3. 请求现代 V2 播放流接口
                var v2Req = new HttpRequestMessage(HttpMethod.Get, $"https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id={realRoomId}&protocol=0,1&format=0,1,2&codec=0,1&qn={qn}&platform=web&ptype=8&dolby=5&panorama=1&hdr_type=0,1");
                v2Req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36");
                v2Req.Headers.Add("Referer", $"https://live.bilibili.com/{realRoomId}");
                if (!string.IsNullOrEmpty(_cookies)) v2Req.Headers.Add("Cookie", _cookies);

                var v2Resp = await _httpClient.SendAsync(v2Req);
                var v2JsonStr = await v2Resp.Content.ReadAsStringAsync();
                var v2Json = JsonNode.Parse(v2JsonStr);

                if (v2Json?["data"]?["live_status"]?.GetValue<int>() == 0)
                    throw new Exception("Not Live (未开播)");

                int code = v2Json?["code"]?.GetValue<int>() ?? 0;
                if (code == -101 || code == -400)
                    throw new Exception("Cookie Invalid (Cookie失效或需登录)");

                var streamArray = v2Json?["data"]?["playurl_info"]?["playurl"]?["stream"]?.AsArray();
                if (streamArray != null && streamArray.Count > 0)
                {
                    // 优先选择 http_stream (FLV) 或 http_hls
                    var selectedStream = streamArray.FirstOrDefault(s => s?["protocol_name"]?.GetValue<string>() == "http_stream") 
                                         ?? streamArray[0];

                    var formatArray = selectedStream?["format"]?.AsArray();
                    if (formatArray != null && formatArray.Count > 0)
                    {
                        var selectedFormat = formatArray.FirstOrDefault(f => f?["format_name"]?.GetValue<string>() == "flv") 
                                             ?? formatArray[0];

                        var codecArray = selectedFormat?["codec"]?.AsArray();
                        if (codecArray != null && codecArray.Count > 0)
                        {
                            // 优先 AVC (h264) 格式，兼容性最好且码率满速
                            var selectedCodec = codecArray.FirstOrDefault(c => c?["codec_name"]?.GetValue<string>() == "avc") 
                                                ?? codecArray[0];

                            string baseUrl = selectedCodec?["base_url"]?.GetValue<string>() ?? "";
                            var urlInfoArray = selectedCodec?["url_info"]?.AsArray();
                            if (urlInfoArray != null && urlInfoArray.Count > 0)
                            {
                                // 优先选 gotcha 节点
                                var selectedUrlInfo = urlInfoArray.FirstOrDefault(u => u?["host"]?.GetValue<string>()?.Contains("gotcha") == true)
                                                      ?? urlInfoArray[0];

                                string host = selectedUrlInfo?["host"]?.GetValue<string>() ?? "";
                                string extra = selectedUrlInfo?["extra"]?.GetValue<string>() ?? "";
                                return host + baseUrl + extra;
                            }
                        }
                    }
                }

                throw new Exception("Not Live (未开播)");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Not Live") || ex.Message.Contains("Cookie Invalid")) throw;
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
                    if (!string.IsNullOrEmpty(_cookies)) req.Headers.Add("Cookie", _cookies);
                    
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

                // 1. 获取动态加密密钥 (带 Cookie 鉴权)
                var encReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.douyu.com/wgapi/livenc/liveweb/websec/getEncryption?did=10000000000000000000000000001501");
                encReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36");
                encReq.Headers.Add("Referer", $"https://www.douyu.com/{roomId}");
                if (!string.IsNullOrEmpty(_cookies)) encReq.Headers.Add("Cookie", _cookies);
                
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

                // 2. 请求播放地址 (带全量 Cookie，解锁原画2K60/4K与最高码率)
                var playReq = new HttpRequestMessage(HttpMethod.Post, $"https://playweb.douyucdn.cn/lapi/live/getH5PlayV1/{roomId}");
                playReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36");
                playReq.Headers.Add("Origin", "https://www.douyu.com");
                playReq.Headers.Add("Referer", $"https://www.douyu.com/{roomId}");
                if (!string.IsNullOrEmpty(_cookies)) playReq.Headers.Add("Cookie", _cookies);
                playReq.Content = new FormUrlEncodedContent(paramDict);

                var playRes = await _httpClient.SendAsync(playReq);
                var playJson = JsonNode.Parse(await playRes.Content.ReadAsStringAsync());
                
                int errCode = playJson?["error"]?.GetValue<int>() ?? -1;
                if (errCode == 0)
                {
                    var data = playJson?["data"];
                    string rtmpUrl = data?["rtmp_url"]?.GetValue<string>() ?? "";
                    string rtmpLive = data?["rtmp_live"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(rtmpUrl) && !string.IsNullOrEmpty(rtmpLive))
                    {
                        return $"{rtmpUrl}/{rtmpLive}";
                    }
                }
                else if (errCode == 102 || errCode == 104)
                {
                    throw new Exception("Not Live (未开播)");
                }
                else if (errCode == -5 || errCode == 51)
                {
                    throw new Exception("Cookie Invalid (Cookie失效或需登录)");
                }

                throw new Exception("Not Live (未开播)");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Not Live") || ex.Message.Contains("Cookie Invalid")) throw;
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
        private string _ratioParam = "";

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
            return $"{_sHlsUrl}/{_sStreamName}.{_sHlsUrlSuffix}?{newAntiCode}&ratio={_ratioParam}";
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
                // 1. 码率参数映射
                _ratioParam = quality switch
                {
                    "OD" => "",
                    "BD" => "",
                    "UHD" => "4000",
                    "HD" => "2000",
                    "SD" => "1000",
                    _ => ""
                };

                // 2. 解析房间号 (支持别名转换)
                string roomId = url.Split('?')[0].TrimEnd('/').Split('/').Last();
                if (roomId.Any(char.IsLetter))
                {
                    var mReq = new HttpRequestMessage(HttpMethod.Get, url.StartsWith("http") ? url : $"https://m.huya.com/{roomId}");
                    mReq.Headers.Add("User-Agent", "ios/7.830 (ios 17.0; ; iPhone 15)");
                    if (!string.IsNullOrEmpty(_cookies)) mReq.Headers.Add("Cookie", _cookies);
                    
                    var mRes = await _httpClient.SendAsync(mReq);
                    var html = await mRes.Content.ReadAsStringAsync();
                    var match = Regex.Match(html, @"ProfileRoom"":(.*?),""sPrivateHost");
                    if (match.Success) roomId = match.Groups[1].Value;
                }

                // 3. 请求虎牙移动端 Profile 缓存接口 (带 Cookie)
                var req = new HttpRequestMessage(HttpMethod.Get, $"https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid={roomId}&showSecret=1");
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0");
                req.Headers.Add("Referer", $"https://www.huya.com/{roomId}");
                if (!string.IsNullOrEmpty(_cookies)) req.Headers.Add("Cookie", _cookies);
                
                var res = await _httpClient.SendAsync(req);
                var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());

                string status = json?["data"]?["realLiveStatus"]?.GetValue<string>() ?? "";
                if (status != "ON") throw new Exception("Not Live (未开播)");

                string liveType = json?["data"]?["liveData"]?["gameHostName"]?.GetValue<string>() ?? "";
                if (liveType == "lol")
                {
                    // LOL 分区优先走 PC 页面流解析
                    var pcReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.huya.com/{roomId}");
                    pcReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0");
                    if (!string.IsNullOrEmpty(_cookies)) pcReq.Headers.Add("Cookie", _cookies);
                    
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
                            return $"{_sHlsUrl}/{_sStreamName}.{_sHlsUrlSuffix}?{newAntiCode}&ratio={_ratioParam}";
                        }
                    }
                }

                // 4. 多 CDN 优先级优选 (TX > HW > AL > HS > 首选)
                var baseSteamInfoList = json?["data"]?["stream"]?["baseSteamInfoList"]?.AsArray();
                if (baseSteamInfoList != null && baseSteamInfoList.Count > 0)
                {
                    var selectedItem = baseSteamInfoList.FirstOrDefault(x => x?["sCdnType"]?.GetValue<string>() == "TX")
                                       ?? baseSteamInfoList.FirstOrDefault(x => x?["sCdnType"]?.GetValue<string>() == "HW")
                                       ?? baseSteamInfoList.FirstOrDefault(x => x?["sCdnType"]?.GetValue<string>() == "AL")
                                       ?? baseSteamInfoList[0];

                    if (selectedItem != null)
                    {
                        _sStreamName = selectedItem["sStreamName"]?.GetValue<string>() ?? "";
                        _sHlsUrl = selectedItem["sHlsUrl"]?.GetValue<string>() ?? "";
                        _sHlsUrlSuffix = selectedItem["sHlsUrlSuffix"]?.GetValue<string>() ?? "m3u8";
                        _sHlsAntiCode = selectedItem["sHlsAntiCode"]?.GetValue<string>() ?? "";
                        string newAntiCode = GetAntiCode(_sHlsAntiCode, _sStreamName);
                        string m3u8Url = $"{_sHlsUrl}/{_sStreamName}.{_sHlsUrlSuffix}?{newAntiCode}&ratio={_ratioParam}";
                        return m3u8Url;
                    }
                }
                
                throw new Exception("Cookie Invalid (Cookie失效或需登录)");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Not Live") || ex.Message.Contains("Cookie Invalid")) throw;
                throw new Exception($"Huya Error: {ex.Message}");
            }
        }
    }
}
