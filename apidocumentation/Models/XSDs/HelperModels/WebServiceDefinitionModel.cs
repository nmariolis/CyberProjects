using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace Avra.Travel.Domain.Documentation.HelperModels
{
    public class WebServiceDefinitionModel
    {
        public Service GeneralServiceInfo { get; private set; }

        public WebServiceDetails Request { get; private set; }

        public WebServiceDetails Response { get; private set; }

        public WebServiceDefinitionModel(WebServiceDetails request, WebServiceDetails response)
        {
            Request = request;
            Response = response;
        }

        public WebServiceDefinitionModel(WebServiceDetails request)
        {
            Request = request;
        }

        public void SetGeneralServiceInfo(Service service)
        {
            GeneralServiceInfo = service;
        }

        public void InjectUserCredentials(string username, string password)
        {
            Request.SetXml(Request.Xml.Replace("<Username>username</Username>", "<Username>" + username + "</Username>"));
            Request.SetXml(Request.Xml.Replace("<Password>password</Password>", "<Password>" + password + "</Password>"));

            var usernameDataObject = Request.ChildObjects.First().Value[0];
            var passwordDataObject = Request.ChildObjects.First().Value[1];

            if(usernameDataObject.Name == "Username")
                usernameDataObject.SetValue(username);

            if (passwordDataObject.Name == "Password")
                passwordDataObject.SetValue(password);
        }
    }

    public class WebServiceDetails
    {

        public string Name { get; private set; }

        public string Xml { get; private set; }

        public XSDDataDictionary ChildObjects { get; private set; }

        public WebServiceDetails(string name, string xml)
        {
            Name = name;
            Xml = xml;

            ChildObjects = new XSDDataDictionary();
        }

        public void AddChildObject(string name, XSDDataObject childObject)
        {
            if (!ChildObjects.ContainsKey(name))
                ChildObjects[name] = new List<XSDDataObject>();
            ChildObjects[name].Add(childObject);
        }

        public void SetXml(string xml)
        {
            Xml = xml;
        }
    }

    public class XSDDataDictionary : Dictionary<string, List<XSDDataObject>>
    {

    }

    public class XSDDataObject
    {
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Type { get; private set; }
        public string Value { get; private set; }
        public bool Required { get; private set; }
        public bool IsAttribute { get; private set; }
        public bool IsComplex { get; private set; }
        public bool IsSimple { get; private set; }
        public string Comment { get; private set; }
        public List<XSDDataObject> ChildObjects { get; set; }
        public SimpleTypeElement SimpleElement { get; private set; }

        public XSDDataObject(XmlNode node, bool isAttribute)
        {
            var name = node.Attributes["name"];
            var description = node.Attributes["xmlns:doc"];
            var type = node.Attributes["type"];
            var maxOccurs = node.Attributes["maxOccurs"];

            if (name != null)
                Name = name.Value;

            if (description != null)
                Description = description.Value;
            //if is complex, type is in complexType -> sequence -> element("type")
            if (type != null)
            {
                SetType(type.Value, maxOccurs);
            }
            SetIsRequired(node, isAttribute);
        }

        public void SetComment(string comment)
        {
            Comment = comment;
        }

        public void SetValue(string value)
        {
            Value = value;
        }

        public void SetIsComplex(bool isComplex)
        {
            IsComplex = isComplex;

            ChildObjects = new List<XSDDataObject>();
        }

        public void SetIsSimple(bool isSimple)
        {
            IsSimple = isSimple;
        }

        public void SetType(string type, XmlAttribute maxOccurs)
        {
            var isArray = false;

            if (maxOccurs != null && (maxOccurs.Value == "unbounded" || int.Parse(maxOccurs.Value) > 1))
                isArray = true;

            if (type.Contains("xs:"))
                Type = type.Substring(3);
            else
                Type = type;

            if (isArray)
                Type += " [ ]";
        }

        public void AddChildObjects(XSDDataObject xsdDataObject)
        {
            if (ChildObjects != null)
                ChildObjects.Add(xsdDataObject);
        }

        public void SetIsRequired(XmlNode node, bool isAttribute)
        {
            IsAttribute = isAttribute;

            if (IsAttribute)
            {
                var use = node.Attributes["use"];

                if (use != null && use.Value == "required")
                {
                    Required = true;
                }
                else
                {
                    Required = false;
                }
            }
            else
            {
                var minOccurs = node.Attributes["minOccurs"];

                if (minOccurs != null)
                    Required = int.Parse(minOccurs.Value) == 1;
            }
        }

        public void SetSimpleTypeElement(SimpleTypeElement simpleTypeElement)
        {
            SimpleElement = simpleTypeElement;
        }
    }
}
