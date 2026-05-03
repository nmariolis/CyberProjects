using Avra.Travel.Domain.Documentation.HelperModels;
using System.Xml;
using System.Xml.Schema;

namespace Documentation.Services
{
    public interface IWebApiDocService
    {
        WebServiceDefinitionModel CreateModel(XmlDocument xmlDoc, XmlSchema xmlSchema);
    }
}
