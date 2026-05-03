using Avra.Travel.Domain.Documentation.HelperModels;
using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Schema;

namespace Documentation.Services
{
    public class WebApiDocService : IWebApiDocService
    {
        public WebServiceDefinitionModel CreateModel(XmlDocument xmlDoc, XmlSchema xmlSchema)
        {
            var xmlRequest = GenerateXML(xmlDoc);
            var requestModel = DecomposeXSD(xmlDoc, xmlRequest);
            return new WebServiceDefinitionModel(requestModel);
        }

        private string GenerateXML(XmlDocument xmlDoc)
        {
            var nsmgr = new XmlNamespaceManager(xmlDoc.NameTable);
            nsmgr.AddNamespace("xs", "http://www.w3.org/2001/XMLSchema");

            var rootElement = xmlDoc.SelectSingleNode("/xs:schema/xs:element[1]", nsmgr);
            string rootName = string.Empty;
            if (rootElement != null && rootElement.Attributes["name"] != null)
                rootName = rootElement.Attributes["name"].Value;

            var elements = xmlDoc.SelectNodes("//xs:element[@name]", nsmgr);
            if (elements == null) return string.Empty;

            var lines = new List<string> { "<" + rootName + ">" };
            foreach (XmlNode el in elements)
            {
                string name = el.Attributes["name"] != null ? el.Attributes["name"].Value : null;
                if (name == null || name == rootName) continue;

                string defaultVal = string.Empty;
                if (el.Attributes["default"] != null)
                    defaultVal = el.Attributes["default"].Value;
                else if (el.Attributes["fixed"] != null)
                    defaultVal = el.Attributes["fixed"].Value;
                lines.Add("    <" + name + ">" + defaultVal + "</" + name + ">");
            }
            lines.Add("</" + rootName + ">");
            return string.Join(Environment.NewLine, lines);
        }

        private WebServiceDetails DecomposeXSD(XmlDocument xmlDoc, string xml)
        {
            var nsmgr = new XmlNamespaceManager(xmlDoc.NameTable);
            nsmgr.AddNamespace("xs", "http://www.w3.org/2001/XMLSchema");

            #region CreateMainElement
            var mainElement = xmlDoc.SelectSingleNode("/xs:schema/xs:element[1]", nsmgr);
            var mainElementName = mainElement.Attributes["name"].Value;
            var serviceDetails = new WebServiceDetails(mainElementName, xml);

            var elementNodes = mainElement.SelectNodes("xs:complexType/xs:sequence/xs:element", nsmgr);
            var attributeNodes = mainElement.SelectNodes("xs:complexType/xs:attribute", nsmgr);

            foreach (XmlNode element in elementNodes)
            {
                var xsdDataObject = new XSDDataObject(element, false);

                var commentNode = element.SelectSingleNode("xs:annotation/xs:documentation", nsmgr);
                if (commentNode != null)
                    xsdDataObject.SetComment(commentNode.InnerText);

                if (element.Attributes["default"] != null)
                    xsdDataObject.SetValue(element.Attributes["default"].Value);
                else if (element.Attributes["fixed"] != null)
                    xsdDataObject.SetValue(element.Attributes["fixed"].Value);

                var complexNodes = element.SelectSingleNode("xs:complexType", nsmgr);
                xsdDataObject.SetIsComplex(complexNodes != null);
                if (element.Attributes["type"] != null)
                    if (!(element.Attributes["type"].Value.Contains("xs:")))
                        xsdDataObject.SetIsComplex(true);

                if (xsdDataObject.IsComplex)
                    HandleComplexTypeElements(xsdDataObject, element, nsmgr);

                var simpleNode = element.SelectSingleNode("xs:simpleType", nsmgr);
                xsdDataObject.SetIsSimple(simpleNode != null);

                if (xsdDataObject.IsSimple)
                    HandleSimpleTypeElements(xsdDataObject, simpleNode, nsmgr);

                serviceDetails.AddChildObject(mainElementName, xsdDataObject);
            }

            foreach (XmlNode attribute in attributeNodes)
            {
                var xsdDataObject = new XSDDataObject(attribute, true);

                var commentNode = attribute.SelectSingleNode("xs:annotation/xs:documentation", nsmgr);
                if (commentNode != null)
                    xsdDataObject.SetComment(commentNode.InnerText);

                if (attribute.Attributes["fixed"] != null)
                    xsdDataObject.SetValue(attribute.Attributes["fixed"].Value);

                var simpleNode = attribute.SelectSingleNode("xs:simpleType", nsmgr);
                xsdDataObject.SetIsSimple(simpleNode != null);

                if (xsdDataObject.IsSimple)
                    HandleSimpleTypeElements(xsdDataObject, simpleNode, nsmgr);

                serviceDetails.AddChildObject(mainElementName, xsdDataObject);
            }
            #endregion

            #region CreateTemplates
            var typeTemplates = xmlDoc.SelectNodes("/xs:schema/xs:complexType", nsmgr);

            foreach (XmlNode typeTemplate in typeTemplates)
            {
                var typeName = typeTemplate.Attributes["name"].Value;
                var typeElements = typeTemplate.SelectNodes("xs:sequence/xs:element", nsmgr);
                var typeAttributes = typeTemplate.SelectNodes("xs:attribute", nsmgr);

                foreach (XmlNode element in typeElements)
                {
                    var xsdDataObject = new XSDDataObject(element, false);

                    var commentNode = element.SelectSingleNode("xs:annotation/xs:documentation", nsmgr);
                    if (commentNode != null)
                        xsdDataObject.SetComment(commentNode.InnerText);

                    if (element.Attributes["default"] != null)
                        xsdDataObject.SetValue(element.Attributes["default"].Value);
                    else if (element.Attributes["fixed"] != null)
                        xsdDataObject.SetValue(element.Attributes["fixed"].Value);

                    var complexNodes = element.SelectSingleNode("xs:complexType", nsmgr);
                    xsdDataObject.SetIsComplex(complexNodes != null);

                    if (xsdDataObject.IsComplex)
                        HandleComplexTypeElements(xsdDataObject, element, nsmgr);

                    var simpleNode = element.SelectSingleNode("xs:simpleType", nsmgr);
                    xsdDataObject.SetIsSimple(simpleNode != null);

                    if (xsdDataObject.IsSimple)
                        HandleSimpleTypeElements(xsdDataObject, simpleNode, nsmgr);

                    serviceDetails.AddChildObject(typeName, xsdDataObject);
                }

                foreach (XmlNode attribute in typeAttributes)
                {
                    var xsdDataObject = new XSDDataObject(attribute, true);

                    var commentNode = attribute.SelectSingleNode("xs:annotation/xs:documentation", nsmgr);
                    if (commentNode != null)
                        xsdDataObject.SetComment(commentNode.InnerText);

                    var simpleNode = attribute.SelectSingleNode("xs:simpleType", nsmgr);
                    xsdDataObject.SetIsSimple(simpleNode != null);
                    if (xsdDataObject.IsSimple)
                        HandleSimpleTypeElements(xsdDataObject, simpleNode, nsmgr);

                    if (attribute.Attributes["fixed"] != null)
                        xsdDataObject.SetValue(attribute.Attributes["fixed"].Value);

                    serviceDetails.AddChildObject(typeName, xsdDataObject);
                }
            }
            #endregion

            return serviceDetails;
        }

        private void HandleComplexTypeElements(XSDDataObject xsdDataObject, XmlNode element, XmlNamespaceManager nsmgr)
        {
            var childElementNodes = element.SelectNodes("xs:complexType/xs:sequence/xs:element", nsmgr);
            var childAttributeNodes = element.SelectNodes("xs:complexType/xs:attribute", nsmgr);

            foreach (XmlNode childElement in childElementNodes)
            {
                var maxOccurs = childElement.Attributes["maxOccurs"];
                var typeAttr = childElement.Attributes["type"];
                if (typeAttr != null)
                    xsdDataObject.SetType(typeAttr.Value, maxOccurs);

                var childXsdDataObject = new XSDDataObject(childElement, false);
                xsdDataObject.AddChildObjects(childXsdDataObject);
            }
        }

        private void HandleSimpleTypeElements(XSDDataObject xsdDataObject, XmlNode simpleNode, XmlNamespaceManager nsmgr)
        {
            xsdDataObject.SetType(simpleNode.SelectSingleNode("xs:restriction", nsmgr).Attributes["base"].Value.Substring(3), null);
            xsdDataObject.SetSimpleTypeElement(CreateSimpleElement(simpleNode, nsmgr));
        }

        private SimpleTypeElement CreateSimpleElement(XmlNode simpleTypeElement, XmlNamespaceManager nsmgr)
        {
            var restrictionElement = simpleTypeElement.SelectSingleNode("xs:restriction", nsmgr);
            var restriction = restrictionElement.FirstChild.LocalName;
            SimpleTypeElement simpleTypeNodeModel = new SimpleTypeElement();
            switch (restriction)
            {
                case "pattern":
                    simpleTypeNodeModel = new SimpleTypeElement(new PatternClass(restrictionElement.SelectSingleNode("xs:pattern", nsmgr).Attributes["value"].Value));
                    break;
                case "minInclusive":
                    simpleTypeNodeModel = new SimpleTypeElement(new MinMaxClass(
                        restrictionElement.SelectSingleNode("xs:minInclusive", nsmgr).Attributes["value"].Value,
                        restrictionElement.SelectSingleNode("xs:maxInclusive", nsmgr).Attributes["value"].Value));
                    break;
                case "enumeration":
                    var enums = restrictionElement.SelectNodes("xs:enumeration", nsmgr);
                    var enumList = new List<string>();
                    foreach (XmlNode enumValue in enums)
                        enumList.Add(enumValue.Attributes["value"].Value);
                    simpleTypeNodeModel = new SimpleTypeElement(new EnumClass(enumList));
                    break;
            }
            return simpleTypeNodeModel;
        }
    }
}
