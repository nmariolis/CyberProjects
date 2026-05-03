using Documentation.Models;
using Documentation.Services;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Web;
using System.Web.Http;
using System.Xml;
using System.Xml.Schema;

namespace Documentation.Controllers
{
    public class WebservicesController : ApiController
    {
        private static readonly string XsdRootFolder = "~/Models/XSDs/XSDs/";
        private readonly IWebApiDocService _webApiDocService = new WebApiDocService();

        [HttpGet]
        [Route("api/webservices/xsd")]
        public IHttpActionResult GetXsd(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return BadRequest("file parameter is required.");

            var rootPath = HttpContext.Current.Server.MapPath(XsdRootFolder);
            var filePath = FindXsdFile(rootPath, file);

            if (filePath == null)
                return NotFound();

            try
            {
                var xmlSchema = new XmlSchema();
                using (var reader = XmlReader.Create(filePath))
                    xmlSchema = XmlSchema.Read(reader, null);

                var xmlDoc = new XmlDocument();
                xmlDoc.Load(filePath);

                var model = _webApiDocService.CreateModel(xmlDoc, xmlSchema);
                return Ok(model);
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        [HttpGet]
        [Route("api/webservices/download")]
        public HttpResponseMessage DownloadXsd(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return Request.CreateErrorResponse(HttpStatusCode.BadRequest, "file parameter is required.");

            var rootPath = HttpContext.Current.Server.MapPath(XsdRootFolder);
            var filePath = FindXsdFile(rootPath, file);

            if (filePath == null)
                return Request.CreateErrorResponse(HttpStatusCode.NotFound, "File not found.");

            var bytes = File.ReadAllBytes(filePath);
            var result = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            result.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = Path.GetFileName(filePath)
            };
            return result;
        }

        [HttpPost]
        [Route("api/webservices/proxy")]
        public IHttpActionResult ProxyXmlRequest([FromBody] ProxyRequest proxyReq)
        {
            if (proxyReq == null || string.IsNullOrWhiteSpace(proxyReq.Url) || string.IsNullOrWhiteSpace(proxyReq.Xml))
                return BadRequest("url and xml are required.");

            if (!IsAllowedUrl(proxyReq.Url))
                return BadRequest("Invalid service URL.");

            try
            {
                var url = proxyReq.Url.Trim();

                // Extract method name (e.g. "HotelSearch" from ".../webservice.asmx/HotelSearch")
                var lastSlash = url.LastIndexOf('/');
                var methodName = lastSlash >= 0 ? url.Substring(lastSlash + 1) : string.Empty;
                var baseUrl = (lastSlash >= 0 && methodName.Length > 0)
                    ? url.Substring(0, lastSlash)
                    : url;

                // Embed the XML as a string parameter inside a SOAP 1.1 envelope
                var escapedXml = SecurityElement.Escape(proxyReq.Xml);
                var soapAction = "http://www.cyberlogic.gr/webservices/CyberlogicReservations/" + methodName;
                var soapEnvelope =
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<soap:Envelope" +
                    " xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"" +
                    " xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"" +
                    " xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                    "<soap:Body>" +
                    "<" + methodName + " xmlns=\"http://www.cyberlogic.gr/webservices/\">" +
                    "<xml>" + escapedXml + "</xml>" +
                    "</" + methodName + ">" +
                    "</soap:Body>" +
                    "</soap:Envelope>";

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    var req = new HttpRequestMessage(HttpMethod.Post, baseUrl);
                    req.Content = new StringContent(soapEnvelope, System.Text.Encoding.UTF8, "text/xml");
                    req.Headers.Add("SOAPAction", "\"" + soapAction + "\"");
                    var response = client.SendAsync(req).Result;
                    var responseText = response.Content.ReadAsStringAsync().Result;
                    return Ok(responseText);
                }
            }
            catch (Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        private bool IsAllowedUrl(string url)
        {
            var u = (url ?? string.Empty).Trim();
            try
            {
                var path = HttpContext.Current.Server.MapPath("~/Models/endpoints.json");
                if (!File.Exists(path)) return false;
                var config = JsonConvert.DeserializeObject<EndpointsConfig>(File.ReadAllText(path));
                return config != null && config.Endpoints != null &&
                       config.Endpoints.Any(e => u.StartsWith(e.BaseUrl.TrimEnd('/') + "/",
                           StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private string FindXsdFile(string rootPath, string file)
        {
            if (file.Contains("/") || file.Contains("\\"))
            {
                var fullPath = Path.Combine(rootPath, file.Replace("/", "\\"));
                return File.Exists(fullPath) ? fullPath : null;
            }
            return Directory
                .EnumerateFiles(rootPath, file, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
    }

    public class ProxyRequest
    {
        public string Url { get; set; }
        public string Xml { get; set; }
    }
}
