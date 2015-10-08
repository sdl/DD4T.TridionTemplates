﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using DD4T.ContentModel;
using Tridion.ContentManager;
using Tridion.ContentManager.Templating;
using Tridion.ExternalContentLibrary.V2;

namespace DD4T.Templates.Base.Utils
{
    internal class EclProcessor : IDisposable
    {
        private readonly TemplatingLogger _log = TemplatingLogger.GetLogger(typeof(EclProcessor));
        private IEclSession _eclSession;

        internal EclProcessor(Session session)
        {
            _eclSession = SessionFactory.CreateEclSession(session);
        }

        internal void ProcessEclStubComponent(Component eclStubComponent)
        {
            IContentLibraryContext eclContext;
            IContentLibraryMultimediaItem eclItem = GetEclItem(eclStubComponent.Id, out eclContext);

            // This may look a bit unusual, but we have to ensure that ECL Item members are accessed *before* the ECL Context is disposed.
            using (eclContext)
            {
                eclStubComponent.EclId = eclItem.Id.ToString();
                eclStubComponent.Multimedia.Url = eclItem.GetDirectLinkToPublished(null);

                // Set additional ECL Item properties as ExtensionData on the ECL Stub Component.
                const string eclSectionName = "ECL";
                eclStubComponent.AddExtensionProperty(eclSectionName, "DisplayTypeId", eclItem.DisplayTypeId);
                eclStubComponent.AddExtensionProperty(eclSectionName, "MimeType", eclItem.MimeType);
                eclStubComponent.AddExtensionProperty(eclSectionName, "FileName", eclItem.Filename);
                eclStubComponent.AddExtensionProperty(eclSectionName, "TemplateFragment", eclItem.GetTemplateFragment(null));

                IFieldSet eclExternalMetadataFieldSet = BuildExternalMetadataFieldSet(eclItem);
                if (eclExternalMetadataFieldSet != null)
                {
                    eclStubComponent.ExtensionData["ECL-ExternalMetadata"] = eclExternalMetadataFieldSet;
                }
            }
        }

        internal string ProcessEclXlink(XmlElement xlinkElement)
        {
            string eclStubComponentId = xlinkElement.GetAttribute("href", "http://www.w3.org/1999/xlink");

            IContentLibraryContext eclContext;
            IContentLibraryMultimediaItem eclItem = GetEclItem(eclStubComponentId, out eclContext);

            // This may look a bit unusual, but we have to ensure that ECL Item members are accessed *before* the ECL Context is disposed.
            using (eclContext)
            {
                // Set additional ECL Item properties as data attributes on the XLink element
                xlinkElement.SetAttribute("data-eclId", eclItem.Id.ToString());
                xlinkElement.SetAttribute("data-eclDisplayTypeId", eclItem.DisplayTypeId);
                if (!string.IsNullOrEmpty(eclItem.MimeType))
                {
                    xlinkElement.SetAttribute("data-eclMimeType", eclItem.MimeType);
                }
                if (!string.IsNullOrEmpty(eclItem.Filename))
                {
                    xlinkElement.SetAttribute("data-eclFileName", eclItem.Filename);
                }
                string eclTemplateFragment = eclItem.GetTemplateFragment(null);
                if (!string.IsNullOrEmpty(eclTemplateFragment))
                {
                    // Note that the entire Template Fragment gets stuffed in an XHTML attribute.
                    // This may seem scary, but there is no limitation to the size of an XML attribute and the XLink element typically already has content.
                    xlinkElement.SetAttribute("data-eclTemplateFragment", eclTemplateFragment);
                }

                // TODO: ECL external metadata (?)

                return eclItem.GetDirectLinkToPublished(null);
            }
        }

        private IContentLibraryMultimediaItem GetEclItem(string eclStubComponentId, out IContentLibraryContext eclContext)
        {
            _log.Debug("Retrieving ECL item for ECL Stub Component: " + eclStubComponentId);
            IEclUri eclUri = _eclSession.TryGetEclUriFromTcmUri(eclStubComponentId);
            if (eclUri == null)
            {
                throw new Exception("Unable to get ECL URI for ECL Stub Component: " + eclStubComponentId);
            }

            eclContext = _eclSession.GetContentLibrary(eclUri);
            // This is done this way to not have an exception thrown through GetItem, as stated in the ECL API doc.
            // The reason to do this, is because if there is an exception, the ServiceChannel is going into the aborted state.
            // GetItems allows up to 20 (depending on config) connections. 
            IList<IContentLibraryItem> eclItems = eclContext.GetItems(new[] { eclUri });
            IContentLibraryMultimediaItem eclItem = (eclItems == null) ? null : eclItems.OfType<IContentLibraryMultimediaItem>().FirstOrDefault();
            if (eclItem == null)
            {
                eclContext.Dispose();
                throw new Exception(string.Format("ECL item '{0}' not found (TCM URI: '{1}')", eclUri, eclStubComponentId));
            }

            _log.Debug(string.Format("Retrieved ECL item for ECL Stub Component '{0}': {1}", eclStubComponentId, eclUri));
            return eclItem;
        }


        private IFieldSet BuildExternalMetadataFieldSet(IContentLibraryItem eclItem)
        {
            string externalMetadataXml = eclItem.MetadataXml;
            if (string.IsNullOrEmpty(externalMetadataXml))
            {
                // No external metadata available; nothing to do.
                return null;
            }

            ISchemaDefinition externalMetadataSchema = eclItem.MetadataXmlSchema;
            if (externalMetadataSchema == null)
            {
                _log.Warning(string.Format("ECL Item '{0}' has external metadata, but no schema defining it.", eclItem.Id));
                return null;
            }

            try
            {
                XmlDocument externalMetadataDoc = new XmlDocument();
                externalMetadataDoc.LoadXml(externalMetadataXml);
                IFieldSet result = CreateExternalMetadataFieldSet(externalMetadataSchema.Fields, externalMetadataDoc.DocumentElement);

                _log.Debug(string.Format("ECL Item '{0}' has external metadata: {1}", eclItem.Id, string.Join(", ", result.Keys)));
                return result;
            }
            catch (Exception ex)
            {
                _log.Error("An error occurred while parsing the external metadata for ECL Item " + eclItem.Id);
                _log.Error(ex.Message);
                return null;
            }
        }

        private static FieldSet CreateExternalMetadataFieldSet(IEnumerable<IFieldDefinition> eclFieldDefinitions, XmlElement parentElement)
        {
            FieldSet fieldSet = new FieldSet();
            foreach (IFieldDefinition eclFieldDefinition in eclFieldDefinitions)
            {
                XmlNodeList fieldElements = parentElement.SelectNodes(string.Format("*[local-name()='{0}']", eclFieldDefinition.XmlElementName));
                if (fieldElements.Count == 0)
                {
                    // Don't generate a DD4T Field for ECL field without values.
                    continue;
                }

                Field field = new Field { Name = eclFieldDefinition.XmlElementName };
                foreach (XmlElement fieldElement in fieldElements)
                {
                    if (eclFieldDefinition is INumberFieldDefinition)
                    {
                        field.NumericValues.Add(Convert.ToDouble(fieldElement.InnerText));
                        field.FieldType = FieldType.Number;
                    }
                    else if (eclFieldDefinition is IDateFieldDefinition)
                    {
                        field.DateTimeValues.Add(Convert.ToDateTime(fieldElement.InnerText));
                        field.FieldType = FieldType.Date;
                    }
                    else if (eclFieldDefinition is IFieldGroupDefinition)
                    {
                        if (field.EmbeddedValues == null)
                        {
                            field.EmbeddedValues = new List<FieldSet>();
                        }
                        IEnumerable<IFieldDefinition> embeddedFieldDefinitions = ((IFieldGroupDefinition) eclFieldDefinition).Fields;
                        field.EmbeddedValues.Add(CreateExternalMetadataFieldSet(embeddedFieldDefinitions, fieldElement));
                        field.FieldType = FieldType.Embedded;
                    }
                    else
                    {
                        field.Values.Add(fieldElement.InnerText);
                        if (eclFieldDefinition is IMultiLineTextFieldDefinition)
                        {
                            field.FieldType = FieldType.MultiLineText;
                        }
                        else if (eclFieldDefinition is IXhtmlFieldDefinition)
                        {
                            field.FieldType = FieldType.Xhtml;
                        }
                        else
                        {
                            field.FieldType = FieldType.Text;
                        }
                    }
                }

                fieldSet.Add(eclFieldDefinition.XmlElementName, field);
            }

            return fieldSet;
        }

        public void Dispose()
        {
            if (_eclSession != null)
            {
                _eclSession.Dispose();
                _eclSession = null;
            }
        }
    }
}