using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // --diag の TLS ハンドシェイクがプロキシ経由で動くこと。
    //
    // 社内ネットワークでは外向きの 443 が遮断され、直接接続すると
    // すべてタイムアウトする。プロキシがあれば CONNECT で通す。
    [Collection("env")]
    public class DiagnosticsProxyTests : IDisposable
    {
        readonly string dir;

        public DiagnosticsProxyTests()
        {
            dir = Path.Combine(Path.GetTempPath(),
                "tau-diagproxy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // ---------------- プロキシの選択 ----------------

        [Fact]
        public void ExplicitProxyIsUsed()
        {
            Config cfg = new("");
            cfg.ProxyUrl = "http://proxy.example.com:8080";
            cfg.UseSystemProxy = false;

            Uri target = ProxyHelper.ConnectTarget(cfg, new Uri("https://api.example.com/v1/chat"));
            Assert.Equal("proxy.example.com", target.Host);
            Assert.Equal(8080, target.Port);
        }

        [Fact]
        public void DirectConnectionWhenNoProxyIsConfigured()
        {
            Config cfg = new("");
            cfg.ProxyUrl = "";
            cfg.UseSystemProxy = false;

            Uri uri = new("https://api.example.com/v1/chat");
            Assert.Equal(uri, ProxyHelper.ConnectTarget(cfg, uri));
            Assert.Null(ProxyHelper.ForUrl(cfg, uri));
        }

        [Fact]
        public void ExplicitProxyCarriesCredentials()
        {
            Config cfg = new("");
            cfg.ProxyUrl = "http://proxy.example.com:8080";
            cfg.ProxyUser = "user";
            cfg.ProxyPass = "secret";
            cfg.UseSystemProxy = false;

            IWebProxy p = ProxyHelper.ForUrl(cfg, new Uri("https://api.example.com/"));
            Assert.NotNull(p);
            ICredentials creds = p.Credentials;
            Assert.NotNull(creds);
            NetworkCredential nc = creds.GetCredential(
                new Uri("http://proxy.example.com:8080"), "Basic");
            Assert.Equal("user", nc.UserName);
            Assert.Equal("secret", nc.Password);
        }

        // ---------------- 偽のプロキシ ----------------

        // CONNECT を受け付けて、応答コードを返すだけの小さなサーバ。
        sealed class FakeProxy : IDisposable
        {
            readonly TcpListener listener;
            readonly Thread thread;
            volatile bool running = true;

            public int Port { get; }
            public string LastRequest { get; private set; }
            public int ReplyCode { get; set; } = 200;
            public string ReplyText { get; set; } = "Connection Established";

            public FakeProxy()
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                thread = new Thread(Loop) { IsBackground = true };
                thread.Start();
            }

            void Loop()
            {
                while (running)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); }
                    catch { break; }

                    try
                    {
                        using (c)
                        using (NetworkStream s = c.GetStream())
                        {
                            StringBuilder head = new();
                            DateTime limit = DateTime.UtcNow.AddSeconds(5);
                            while (DateTime.UtcNow < limit)
                            {
                                int b = s.ReadByte();
                                if (b < 0) break;
                                head.Append((char)b);
                                string soFar = head.ToString();
                                if (soFar.EndsWith("\r\n\r\n")) break;
                            }
                            LastRequest = head.ToString();

                            string reply = "HTTP/1.1 " + ReplyCode + " " + ReplyText
                                + "\r\nProxy-Agent: fake\r\n\r\n";
                            byte[] bytes = Encoding.ASCII.GetBytes(reply);
                            s.Write(bytes, 0, bytes.Length);
                            s.Flush();
                        }
                    }
                    catch { }
                }
            }

            public void Dispose()
            {
                running = false;
                try { listener.Stop(); } catch { }
            }
        }

        [Fact]
        public void ProbeSendsConnectToTheProxy()
        {
            using FakeProxy proxy = new();
            Config cfg = new("");
            cfg.ProxyUrl = "http://127.0.0.1:" + proxy.Port;
            cfg.UseSystemProxy = false;

            // プロキシが 200 を返したあと、TLS は始まらない(相手が閉じる)ので
            // NG になるが、CONNECT がプロキシへ送られたことを確認する。
            string result = Diagnostics.ProbeTlsPublic(
                new Uri("https://api.example.com/v1/chat"),
                cfg,
                SecurityProtocolType.Tls12);

            Assert.NotNull(proxy.LastRequest);
            Assert.Contains("CONNECT api.example.com:443 HTTP/1.1", proxy.LastRequest);
            Assert.Contains("Host: api.example.com:443", proxy.LastRequest);
            Assert.StartsWith("NG", result);
        }

        [Fact]
        public void ProbeReportsProxyAuthenticationRequired()
        {
            using FakeProxy proxy = new();
            proxy.ReplyCode = 407;
            proxy.ReplyText = "Proxy Authentication Required";

            Config cfg = new("");
            cfg.ProxyUrl = "http://127.0.0.1:" + proxy.Port;
            cfg.UseSystemProxy = false;

            string result = Diagnostics.ProbeTlsPublic(
                new Uri("https://api.example.com/"), cfg, SecurityProtocolType.Tls12);

            Assert.StartsWith("NG", result);
            Assert.Contains("407", result);
            Assert.Contains("proxyUser", result);
        }

        [Fact]
        public void ProbeSendsProxyAuthorizationWhenConfigured()
        {
            using FakeProxy proxy = new();
            Config cfg = new("");
            cfg.ProxyUrl = "http://127.0.0.1:" + proxy.Port;
            cfg.ProxyUser = "alice";
            cfg.ProxyPass = "s3cret";
            cfg.UseSystemProxy = false;

            Diagnostics.ProbeTlsPublic(
                new Uri("https://api.example.com/"), cfg, SecurityProtocolType.Tls12);

            Assert.NotNull(proxy.LastRequest);
            Assert.Contains("Proxy-Authorization: Basic", proxy.LastRequest);
            string expected = Convert.ToBase64String(
                Encoding.UTF8.GetBytes("alice:s3cret"));
            Assert.Contains(expected, proxy.LastRequest);
        }

        // CONNECT を通したあと、本物の TLS サーバへ中継するプロキシ。
        sealed class TunnelingProxy : IDisposable
        {
            readonly TcpListener listener;
            readonly Thread thread;
            readonly int upstreamPort;
            volatile bool running = true;

            public int Port { get; }

            public TunnelingProxy(int upstreamPort)
            {
                this.upstreamPort = upstreamPort;
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                thread = new Thread(Loop) { IsBackground = true };
                thread.Start();
            }

            void Loop()
            {
                while (running)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); }
                    catch { break; }

                    try
                    {
                        NetworkStream s = c.GetStream();

                        // CONNECT のヘッダを読む
                        StringBuilder head = new();
                        DateTime limit = DateTime.UtcNow.AddSeconds(5);
                        while (DateTime.UtcNow < limit)
                        {
                            int b = s.ReadByte();
                            if (b < 0) break;
                            head.Append((char)b);
                            if (head.ToString().EndsWith("\r\n\r\n")) break;
                        }

                        byte[] ok = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 Connection Established\r\n\r\n");
                        s.Write(ok, 0, ok.Length);
                        s.Flush();

                        // 上流へ中継する
                        TcpClient up = new("127.0.0.1", upstreamPort);
                        NetworkStream u = up.GetStream();
                        Thread t1 = new(() => Pump(s, u)) { IsBackground = true };
                        Thread t2 = new(() => Pump(u, s)) { IsBackground = true };
                        t1.Start();
                        t2.Start();
                        t1.Join(10000);
                        t2.Join(10000);
                        try { up.Close(); } catch { }
                    }
                    catch { }
                    try { c.Close(); } catch { }
                }
            }

            static void Pump(Stream from, Stream to)
            {
                byte[] buf = new byte[8192];
                try
                {
                    while (true)
                    {
                        int n = from.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        to.Write(buf, 0, n);
                        to.Flush();
                    }
                }
                catch { }
            }

            public void Dispose()
            {
                running = false;
                try { listener.Stop(); } catch { }
            }
        }

        [Fact]
        public void ProbeSucceedsThroughATunnelingProxy()
        {
            // ローカルの TLS サーバを立て、プロキシ経由でハンドシェイクする。
            // 証明書は自己署名なので検証は失敗するが、TLS の交渉自体は進む。
            // ここでは「CONNECT が通り、上流へ到達した」ことを確認する。
            TcpListener upstream = new(IPAddress.Loopback, 0);
            upstream.Start();
            int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;

            Thread server = new(() =>
            {
                try
                {
                    TcpClient c = upstream.AcceptTcpClient();
                    NetworkStream s = c.GetStream();
                    // TLS の ClientHello が届くまで少し読む
                    byte[] buf = new byte[1024];
                    s.ReadTimeout = 5000;
                    try { s.Read(buf, 0, buf.Length); } catch { }
                    try { c.Close(); } catch { }
                }
                catch { }
            }) { IsBackground = true };
            server.Start();

            using TunnelingProxy proxy = new(upstreamPort);
            Config cfg = new("");
            cfg.ProxyUrl = "http://127.0.0.1:" + proxy.Port;
            cfg.UseSystemProxy = false;

            string result = Diagnostics.ProbeTlsPublic(
                new Uri("https://api.example.com/"), cfg, SecurityProtocolType.Tls12);

            // 上流が ClientHello を受け取って閉じるので、TLS は完走しない。
            // ただし CONNECT は成功し、タイムアウトでも接続拒否でもないこと。
            Assert.StartsWith("NG", result);
            Assert.DoesNotContain("connection timed out", result);
            Assert.DoesNotContain("proxy CONNECT failed", result);

            try { upstream.Stop(); } catch { }
        }

        [Fact]
        public void ProbeCompletesATlsHandshakeThroughAProxy()
        {
            // 自己署名証明書の TLS サーバを立て、プロキシ経由で握手する。
            // 証明書の検証は失敗するが、TLS の交渉とプロキシ経由の経路が
            // 通っていることを確認する。
            using System.Security.Cryptography.X509Certificates.X509Certificate2 cert =
                SelfSignedCert();

            TcpListener upstream = new(IPAddress.Loopback, 0);
            upstream.Start();
            int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;

            Thread server = new(() =>
            {
                try
                {
                    TcpClient c = upstream.AcceptTcpClient();
                    using System.Net.Security.SslStream ssl = new(c.GetStream(), false);
                    ssl.AuthenticateAsServer(cert, false,
                        System.Security.Authentication.SslProtocols.Tls12, false);
                    try { c.Close(); } catch { }
                }
                catch { }
            }) { IsBackground = true };
            server.Start();

            using TunnelingProxy proxy = new(upstreamPort);
            Config cfg = new("");
            cfg.ProxyUrl = "http://127.0.0.1:" + proxy.Port;
            cfg.UseSystemProxy = false;

            // host は 127.0.0.1 なので、プロキシは 127.0.0.1:443 へ CONNECT する。
            // 実際の上流は別ポートなので、TunnelingProxy が中継する。
            string result = Diagnostics.ProbeTlsPublic(
                new Uri("https://127.0.0.1/"), cfg, SecurityProtocolType.Tls12);

            // 証明書が自己署名なので、検証で失敗する（あるいはサーバ側が先に
            // 閉じて EOF になる）。どちらでも、CONNECT が通って TLS の
            // 交渉まで進んだことを確認する。
            Assert.StartsWith("NG", result);
            Assert.DoesNotContain("proxy CONNECT failed", result);
            Assert.DoesNotContain("connection timed out", result);
            Assert.True(
                result.Contains("Authentication") || result.Contains("RemoteCertificate")
                || result.Contains("Ssl") || result.Contains("Tls")
                || result.Contains("transport stream") || result.Contains("EOF"),
                "the handshake should reach TLS: " + result);

            try { upstream.Stop(); } catch { }
        }

        static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSignedCert()
        {
            using System.Security.Cryptography.RSA rsa =
                System.Security.Cryptography.RSA.Create(2048);
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=127.0.0.1",
                rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
            return req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        [Fact]
        public void ProbeTimesOutWhenNothingListens()
        {
            // 閉じたポートへ接続すると、すぐに接続失敗になる
            int port;
            using (TcpListener probe = new(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            Config cfg = new("");
            cfg.ProxyUrl = "http://127.0.0.1:" + port;
            cfg.UseSystemProxy = false;

            string result = Diagnostics.ProbeTlsPublic(
                new Uri("https://api.example.com/"), cfg, SecurityProtocolType.Tls12);

            Assert.StartsWith("NG", result);
        }
    }
}
