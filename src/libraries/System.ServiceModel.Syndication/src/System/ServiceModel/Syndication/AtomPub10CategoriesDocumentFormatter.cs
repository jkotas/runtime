// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace System.ServiceModel.Syndication
{
    [XmlRoot(ElementName = App10Constants.Categories, Namespace = App10Constants.Namespace)]
    public class AtomPub10CategoriesDocumentFormatter : CategoriesDocumentFormatter, IXmlSerializable
    {
        private readonly Type _inlineDocumentType;
        private readonly int _maxExtensionSize;
        private readonly Type _referencedDocumentType;

        public AtomPub10CategoriesDocumentFormatter() : this(typeof(InlineCategoriesDocument), typeof(ReferencedCategoriesDocument))
        {
        }

        public AtomPub10CategoriesDocumentFormatter(Type inlineDocumentType, Type referencedDocumentType) : base()
        {
            ArgumentNullException.ThrowIfNull(inlineDocumentType);
            if (!typeof(InlineCategoriesDocument).IsAssignableFrom(inlineDocumentType))
            {
                throw new ArgumentException(SR.Format(SR.InvalidObjectTypePassed, nameof(inlineDocumentType), nameof(InlineCategoriesDocument)), nameof(inlineDocumentType));
            }
            ArgumentNullException.ThrowIfNull(referencedDocumentType);
            if (!typeof(ReferencedCategoriesDocument).IsAssignableFrom(referencedDocumentType))
            {
                throw new ArgumentException(SR.Format(SR.InvalidObjectTypePassed, nameof(referencedDocumentType), nameof(ReferencedCategoriesDocument)), nameof(referencedDocumentType));
            }

            _maxExtensionSize = int.MaxValue;
            _inlineDocumentType = inlineDocumentType;
            _referencedDocumentType = referencedDocumentType;
        }

        public AtomPub10CategoriesDocumentFormatter(CategoriesDocument documentToWrite) : base(documentToWrite)
        {
            _maxExtensionSize = int.MaxValue;
            if (documentToWrite.IsInline)
            {
                _inlineDocumentType = documentToWrite.GetType();
                _referencedDocumentType = typeof(ReferencedCategoriesDocument);
            }
            else
            {
                _referencedDocumentType = documentToWrite.GetType();
                _inlineDocumentType = typeof(InlineCategoriesDocument);
            }
        }

        public override string Version => App10Constants.Namespace;

        public override bool CanRead(XmlReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            return reader.IsStartElement(App10Constants.Categories, App10Constants.Namespace);
        }

        XmlSchema IXmlSerializable.GetSchema() => null;

        void IXmlSerializable.ReadXml(XmlReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            ReadDocument(reader);
        }

        void IXmlSerializable.WriteXml(XmlWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (Document == null)
            {
                throw new InvalidOperationException(SR.DocumentFormatterDoesNotHaveDocument);
            }

            WriteDocument(writer);
        }

        public override void ReadFrom(XmlReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            if (!CanRead(reader))
            {
                throw new XmlException(SR.Format(SR.UnknownDocumentXml, reader.LocalName, reader.NamespaceURI));
            }

            ReadDocument(reader);
        }

        public override void WriteTo(XmlWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (Document == null)
            {
                throw new InvalidOperationException(SR.DocumentFormatterDoesNotHaveDocument);
            }

            writer.WriteStartElement(App10Constants.Prefix, App10Constants.Categories, App10Constants.Namespace);
            WriteDocument(writer);
            writer.WriteEndElement();
        }

        protected override InlineCategoriesDocument CreateInlineCategoriesDocument()
        {
            if (_inlineDocumentType == typeof(InlineCategoriesDocument))
            {
                return new InlineCategoriesDocument();
            }
            else
            {
                return (InlineCategoriesDocument)Activator.CreateInstance(_inlineDocumentType);
            }
        }

        protected override ReferencedCategoriesDocument CreateReferencedCategoriesDocument()
        {
            if (_referencedDocumentType == typeof(ReferencedCategoriesDocument))
            {
                return new ReferencedCategoriesDocument();
            }
            else
            {
                return (ReferencedCategoriesDocument)Activator.CreateInstance(_referencedDocumentType);
            }
        }

        private void ReadDocument(XmlReader reader)
        {
            try
            {
                SyndicationFeedFormatter.MoveToStartElement(reader);
                SetDocument(AtomPub10ServiceDocumentFormatter.ReadCategories(reader, null,
                    CreateInlineCategoriesDocument,
                    CreateReferencedCategoriesDocument,
                    Version,
                    _maxExtensionSize));
            }
            catch (FormatException e)
            {
                throw new XmlException(FeedUtils.AddLineInfo(reader, SR.ErrorParsingDocument), e);
            }
            catch (ArgumentException e)
            {
                throw new XmlException(FeedUtils.AddLineInfo(reader, SR.ErrorParsingDocument), e);
            }
        }

        private void WriteDocument(XmlWriter writer)
        {
            // declare the atom10 namespace upfront for compactness
            writer.WriteAttributeString(Atom10Constants.Atom10Prefix, Atom10FeedFormatter.XmlNsNs, Atom10Constants.Atom10Namespace);
            AtomPub10ServiceDocumentFormatter.WriteCategoriesInnerXml(writer, Document, null, Version);
        }
    }
}
