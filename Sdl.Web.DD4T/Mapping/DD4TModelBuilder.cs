﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DD4T.ContentModel;
using Sdl.Web.Mvc.Mapping;
using Sdl.Web.Mvc.Models;
using System.Collections;
using Sdl.Web.Mvc;
using System.Text.RegularExpressions;
using System.Globalization;

namespace Sdl.Web.DD4T.Mapping
{
    public class DD4TModelBuilder : BaseModelBuilder
    {
        public ExtensionlessLinkFactory LinkFactory { get; set; }

        public DD4TModelBuilder()
        {
            LinkFactory = new ExtensionlessLinkFactory();
        }

        public override object Create(object sourceEntity, Type type, List<object> includes = null)
        {
            if (sourceEntity is IPage)
            {
                return CreatePage(sourceEntity, type, includes);
            }
            
            return CreateEntity(sourceEntity, type, includes);
        }
        
        protected virtual object CreateEntity(object sourceEntity, Type type, List<object> includes=null)
        {
            IComponent component = sourceEntity as IComponent;
            Dictionary<string, string> entityData;
            if (component == null && sourceEntity is IComponentPresentation)
            {
                var cp = (IComponentPresentation)sourceEntity;
                component = cp.Component;
                entityData = GetEntityData(cp);
            }
            else
            {
                entityData = GetEntityData(component);
            }
            if (component != null)
            {
                // get schema item id from tcmuri -> tcm:1-2-8
                string[] uriParts = component.Schema.Id.Split('-');
                long schemaId = Convert.ToInt64(uriParts[1]);
                MappingData mapData = new MappingData {SemanticSchema = SemanticMapping.GetSchema(schemaId.ToString())};
                // get schema entity names (indexed by vocabulary)
                mapData.EntityNames = mapData.SemanticSchema.GetEntityNames();
            
                //TODO may need to merge with vocabs from embedded types
                mapData.TargetEntitiesByPrefix = GetEntityDataFromType(type);
                mapData.Content = component.Fields;
                mapData.Meta = component.MetadataFields;
                mapData.TargetType = type;
                mapData.SourceEntity = component;
                var model = CreateModelFromMapData(mapData);
                if (model is Entity)
                {
                    ((Entity)model).EntityData = entityData;
                    ((Entity)model).Id = component.Id.Split('-')[1];
                }
                return model;

            }
            return null;
        }

        protected virtual object CreateModelFromMapData(MappingData mapData)
        {
            var model = Activator.CreateInstance(mapData.TargetType);
            Dictionary<string, string> propertyData = new Dictionary<string, string>();
            var propertySemantics = FilterPropertySematicsByEntity(LoadPropertySemantics(mapData.TargetType),mapData);
            foreach (var pi in mapData.TargetType.GetProperties())
            {
                bool multival = pi.PropertyType.IsGenericType && (pi.PropertyType.GetGenericTypeDefinition() == typeof(List<>));
                Type propertyType = multival ? pi.PropertyType.GetGenericArguments()[0] : pi.PropertyType;
                if (propertySemantics.ContainsKey(pi.Name))
                {
                    foreach (var info in propertySemantics[pi.Name])
                    {
                        IField field = GetFieldFromSemantics(mapData, info);
                        if (field != null && (field.Values.Count > 0 || field.EmbeddedValues.Count > 0))
                        {
                            pi.SetValue(model, GetFieldValues(field, propertyType, multival, mapData));
                            propertyData.Add(pi.Name, GetFieldXPath(field));
                            break;
                        }
                        //Special cases, where we want to map for example the whole entity to an image property, or a resolved link to the entity to a Url field
                        if (info.PropertyName == "_self")
                        {
                            bool processed = false;
                            switch(pi.Name)
                            {
                                case "Image":
                                    if (mapData.SourceEntity != null && mapData.SourceEntity.Multimedia != null)
                                    {
                                        pi.SetValue(model, GetMultiMediaLinks(new List<IComponent> { mapData.SourceEntity }, propertyType, multival));
                                        processed = true;
                                    }
                                    break;
                                case "Link":
                                    if (mapData.SourceEntity != null)
                                    {
                                        pi.SetValue(model, GetMultiComponentLinks(new List<IComponent> { mapData.SourceEntity }, propertyType, multival));
                                    }
                                    break;
                            }
                            if (processed)
                            {
                                break;
                            }
                        }   
                    }
                }
            }
            if (model is Entity)
            {
                ((Entity)model).PropertyData = propertyData;
            }
            return model;
        }

        protected virtual Dictionary<string, List<SemanticProperty>> FilterPropertySematicsByEntity(Dictionary<string, List<SemanticProperty>> semantics, MappingData mapData)
        {
            Dictionary<string, List<SemanticProperty>> filtered = new Dictionary<string, List<SemanticProperty>>();
            foreach (var item in semantics)
            {
                filtered.Add(item.Key, new List<SemanticProperty>());
                List<SemanticProperty> defaultProperties = new List<SemanticProperty>();
                foreach (var prop in item.Value)
                {
                    //Default prefix is always OK, but should be added last
                    if (String.IsNullOrEmpty(prop.Prefix))
                    {
                        defaultProperties.Add(prop);
                    }
                    else
                    {
                        //Filter out any properties belonging to other entities than the source entity
                        var entityData = GetEntityData(prop.Prefix, mapData.TargetEntitiesByPrefix, mapData.ParentDefaultPrefix);
                        if (entityData != null)
                        {
                            if (mapData.EntityNames.Contains(entityData.Value.Key))
                            {
                                if (mapData.EntityNames[entityData.Value.Key].First() == entityData.Value.Value)
                                {
                                    filtered[item.Key].Add(prop);
                                }
                            }
                        }
                    }
                }
                filtered[item.Key].AddRange(defaultProperties);
            }
            return filtered;
        }

        private IField GetFieldFromSemantics(MappingData mapData, SemanticProperty info)
        {
            var entityData = GetEntityData(info.Prefix, mapData.TargetEntitiesByPrefix, mapData.ParentDefaultPrefix);
            if (entityData != null)
            {
                // determine field semantics
                var vocab = entityData.Value.Key;
                string prefix = SemanticMapping.GetPrefix(vocab);
                if (prefix != null)
                {
                    string property = info.PropertyName;
                    string entity = mapData.EntityNames[vocab].FirstOrDefault();
                    if (entity != null)
                    {
                        FieldSemantics fieldSemantics = new FieldSemantics(prefix, entity, property);
                        // locate semantic schema field
                        SemanticSchemaField matchingField = mapData.SemanticSchema.FindFieldBySemantics(fieldSemantics);
                        if (matchingField != null)
                        {
                            return ExtractMatchedField(matchingField, (matchingField.IsMetadata && mapData.Meta!=null) ? mapData.Meta : mapData.Content, mapData.EmbedLevel);
                        }
                    }
                }
            }
            return null;
        }

        private KeyValuePair<string,string>? GetEntityData(string prefix, Dictionary<string,KeyValuePair<string,string>> entityData, string defaultPrefix)
        {
            if (defaultPrefix != null && String.IsNullOrEmpty(prefix))
            {
                prefix = defaultPrefix;
            }
            if (entityData.ContainsKey(prefix))
            {
                return entityData[prefix];
            }
            return null;
        }


        private IField ExtractMatchedField(SemanticSchemaField matchingField, IFieldSet fields, int embedLevel, string path = null)
        {
            if (path==null)
            {
                path = matchingField.Path;
                while (embedLevel >= -1 && path.Contains("/"))
                {
                    int pos = path.IndexOf("/");
                    path = path.Substring(pos+1);
                    embedLevel--;
                }
            }
            var bits = path.Split('/');
            if (fields.ContainsKey(bits[0]))
            {
                if (bits.Length > 1)
                {
                    int pos = path.IndexOf("/");
                    return ExtractMatchedField(matchingField, fields[bits[0]].EmbeddedValues[0], embedLevel, path.Substring(pos + 1));
                }

                return fields[bits[0]];
            }
            return null;
        }

        private object GetFieldValues(IField field, Type propertyType, bool multival, MappingData mapData)
        {
            switch (field.FieldType)
            {
                case (FieldType.Date):
                    return GetDates(field, propertyType, multival);
                case (FieldType.Number):
                    return GetNumbers(field, propertyType, multival);
                case (FieldType.MultiMediaLink):
                    return GetMultiMediaLinks(field, propertyType, multival);
                case (FieldType.ComponentLink):
                    return GetMultiComponentLinks(field, propertyType, multival);
                case (FieldType.Embedded):
                    return GetMultiEmbedded(field, propertyType, multival, mapData);
                case (FieldType.Keyword):
                    return GetMultiKeywords(field, propertyType, multival);
                default:
                    return GetStrings(field, propertyType, multival);
            }
        }

        private static string GetFieldXPath(IField field)
        {
            return field.XPath;
        }

        protected virtual Dictionary<string, string> GetEntityData(IComponent comp)
        {
            var res = new Dictionary<string, string>();
            if (comp != null)
            {
                res.Add("ComponentID", comp.Id);
                res.Add("ComponentModified", comp.RevisionDate.ToString("s"));
            }
            return res;
        }

        protected virtual Dictionary<string, string> GetEntityData(IComponentPresentation cp)
        {
            if (cp != null)
            {
                var res = GetEntityData(cp.Component);
                res.Add("ComponentTemplateID", cp.ComponentTemplate.Id);
                res.Add("ComponentTemplateModified", cp.ComponentTemplate.RevisionDate.ToString("s"));
                return res;
            }
            return new Dictionary<string, string>();
        }

        protected virtual Dictionary<string, string> GetPageData(IPage page)
        {
            var res = new Dictionary<string, string>();
            if (page != null)
            {
                res.Add("PageID", page.Id);
                res.Add("PageModified", page.RevisionDate.ToString("s"));
                res.Add("PageTemplateID", page.PageTemplate.Id);
                res.Add("PageTemplateModified", page.PageTemplate.RevisionDate.ToString("s"));
            }
            return res;
        }

        private static object GetDates(IField field, Type modelType, bool multival)
        {
            if (modelType.IsAssignableFrom(typeof(DateTime)))
            {
                if (multival)
                {
                    return field.DateTimeValues;
                }

                return field.DateTimeValues[0];
            }
            return null;
        }

        private static object GetNumbers(IField field, Type modelType, bool multival)
        {
            if (modelType.IsAssignableFrom(typeof(Double)))
            {
                if (multival)
                {
                    return field.NumericValues;
                }

                return field.NumericValues[0];
            }
            if (modelType.IsAssignableFrom(typeof(Int32)))
            {
                if (multival)
                {
                    return field.NumericValues.Select(d=>(int)Math.Round(d));
                }
                return (int)Math.Round(field.NumericValues[0]);
            }
            return null;
        }

        private static object GetMultiMediaLinks(IField field, Type modelType, bool multival)
        {
            return GetMultiMediaLinks(field.LinkedComponentValues, modelType, multival);
        }


        private static object GetMultiMediaLinks(IEnumerable<IComponent> items, Type modelType, bool multival)
        {
            var components = items as IList<IComponent> ?? items.ToList();
            if (components.Any())
            {
                // TODO find better way to determine image or video
                string schemaTitle = components.First().Schema.Title;
                if (modelType.IsAssignableFrom(typeof(YouTubeVideo)) && schemaTitle.ToLower().Contains("youtube"))
                {
                    if (multival)
                    {
                        return GetYouTubeVideos(components);
                    }

                    return GetYouTubeVideos(components)[0];
                }
                if (modelType.IsAssignableFrom(typeof(Image)))
                {
                    if (multival)
                    {
                        return GetImages(components);
                    }

                    return GetImages(components)[0];
                }
                // TODO handle other types
            }
            return null;
        }


        private object GetMultiKeywords(IField field, Type linkedItemType, bool multival)
        {
            return GetMultiKeywords(field.Keywords, linkedItemType, multival);
        }

        private object GetMultiKeywords(IList<IKeyword> items, Type linkedItemType, bool multival)
        {
            //What to do depends on the target type
            if (linkedItemType == typeof(Tag))
            {
                List<Tag> res = items.Select(k => new Tag(){DisplayText=GetKeywordDisplayText(k),Key=GetKeywordKey(k),TagCategory=k.TaxonomyId}).ToList();
                if (multival)
                {
                    return res;
                }
                else
                {
                    return res[0];
                }
            } 
            if (linkedItemType == typeof(bool))
            {
                //For booleans we assume the keyword key or value can be converted to bool
                List<bool> res = new List<bool>();
                foreach (var kw in items)
                {
                    bool val = false;
                    Boolean.TryParse(String.IsNullOrEmpty(kw.Key) ? kw.Title : kw.Key, out val);
                    res.Add(val);
                }
                if (multival)
                {
                    return res;
                }
                else
                {
                    return res[0];
                }
            }
            else if (linkedItemType == typeof(String))
            {
                List<String> res = items.Select(k=>GetKeywordDisplayText(k)).ToList();
                if (multival)
                {
                    return res;
                }
                else
                {
                    return res[0];
                }
            }
            return null;
        }

        private string GetKeywordKey(IKeyword k)
        {
            return !String.IsNullOrEmpty(k.Key) ? k.Key : k.Id;
        }

        private string GetKeywordDisplayText(IKeyword k)
        {
            return !String.IsNullOrEmpty(k.Description) ? k.Description : k.Title;
        }
        
        private object GetMultiComponentLinks(IField field, Type linkedItemType, bool multival)
        {
            return GetMultiComponentLinks(field.LinkedComponentValues, linkedItemType, multival);
        }

        private object GetMultiComponentLinks(IEnumerable<IComponent> items, Type linkedItemType, bool multival)
        {
            //What to do depends on the target type
            if (linkedItemType == typeof(String) || linkedItemType == typeof(Link))
            {
                //For strings and Links, we simply resolve the link to a URL
                List<String> urls = new List<String>();
                foreach (var comp in items)
                {
                    var url = LinkFactory.ResolveExtensionlessLink(comp.Id);
                    if (url != null)
                    {
                        urls.Add(url);
                    }
                }
                if (urls.Count == 0)
                {
                    return null;
                }
                if (linkedItemType == typeof(Link))
                {
                    if (multival)
                    {
                        return urls.Select(u => new Link { Url = u }).ToList();
                    }
                    return new Link { Url = urls[0] };
                }
                if (multival)
                {
                    return urls;
                }
                
                return urls[0];
            }

            //TODO is reflection the only way to do this?
            MethodInfo method = GetType().GetMethod("GetCompLink" + (multival ? "s" : ""), BindingFlags.NonPublic | BindingFlags.Instance);
            method = method.MakeGenericMethod(new[] { linkedItemType });
            return method.Invoke(this, new object[] { items, linkedItemType });
        }

        private object GetMultiEmbedded(IField field, Type propertyType, bool multival, MappingData mapData)
        {
            MappingData embedMapData = new MappingData
                {
                    TargetType = propertyType,
                    Meta = null,
                    EntityNames = mapData.EntityNames,
                    ParentDefaultPrefix = mapData.ParentDefaultPrefix,
                    TargetEntitiesByPrefix = mapData.TargetEntitiesByPrefix,
                    SemanticSchema = mapData.SemanticSchema,
                    EmbedLevel = mapData.EmbedLevel + 1
                };
            //This is a bit weird, but necessary as we cannot return List<object>, we need to get the right type
            var result = (IList)typeof(List<>).MakeGenericType(propertyType).GetConstructor(Type.EmptyTypes).Invoke(null);
            foreach (IFieldSet fields in field.EmbeddedValues)
            {
                embedMapData.Content = fields;
                var model = CreateModelFromMapData(embedMapData);
                if (model != null)
                {
                    result.Add(model);
                }
            }
            return result.Count == 0 ? null : (multival ? result : result[0]);

        }

        private static object GetStrings(IField field, Type modelType, bool multival)
        {
            if (modelType.IsAssignableFrom(typeof(String)))
            {
                if (multival)
                {
                    return field.Values;
                }

                return field.Value;
            }
            return null;
        }

        private static List<Image> GetImages(IEnumerable<IComponent> components)
        {
            return components.Select(c => new Image { Url = c.Multimedia.Url, FileSize = c.Multimedia.Size }).ToList();
        }

        private static List<YouTubeVideo> GetYouTubeVideos(IEnumerable<IComponent> components)
        {
            return components.Select(c => new YouTubeVideo { Url = c.Multimedia.Url, FileSize = c.Multimedia.Size, YouTubeId = c.MetadataFields["youTubeId"].Value }).ToList();
        }

        //private List<T> GetCompLinks<T>(IEnumerable<IComponent> components, Type linkedItemType)
        //{
        //    List<T> list = new List<T>();
        //    foreach (var comp in components)
        //    {
        //        list.Add((T)Create(comp, linkedItemType));
        //    }
        //    return list;
        //}

        //private T GetCompLink<T>(IEnumerable<IComponent> components, Type linkedItemType)
        //{
        //    return GetCompLinks<T>(components,linkedItemType)[0];
        //}

        private ResourceProvider _resourceProvider;

        protected virtual object CreatePage(object sourceEntity, Type type, List<object> includes)
        {
            IPage page = sourceEntity as IPage;
            if (page != null)
            {
                string postfix = String.Format(" {0} {1}", GetResource("core.pageTitleSeparator"), GetResource("core.pageTitlePostfix"));

                // strip possible numbers from title
                string title = Regex.Replace(page.Title, @"^\d{3}\s", String.Empty);
                // Index and Default are not a proper titles for an HTML page
                if (title.ToLowerInvariant().Equals("index") || title.ToLowerInvariant().Equals("default"))
                {
                    title = GetResource("core.defaultPageTitle") + postfix;
                }
                WebPage model = new WebPage
                    {
                        Title = title,
                        Meta = ProcessPageMetadata(page),
                        PageData = GetPageData(page)
                    };
                bool first = true;
                foreach (var cp in page.ComponentPresentations)
                {
                    string regionName = GetRegionFromComponentPresentation(cp);
                    if (!model.Regions.ContainsKey(regionName))
                    {
                        model.Regions.Add(regionName, new Region { Name = regionName });
                    }
                    model.Regions[regionName].Items.Add(cp);

                    // determine title and description from first component in 'main' region
                    if (first && regionName.Equals(Configuration.RegionForPageTitleComponent))
                    {
                        first = false;
                        IFieldSet metadata = cp.Component.MetadataFields;
                        IFieldSet fields = cp.Component.Fields;
                        // determine title
                        if (metadata.ContainsKey(Configuration.StandardMetadataXmlFieldName) && metadata[Configuration.StandardMetadataXmlFieldName].EmbeddedValues.Count > 0)
                        {
                            IFieldSet standardMeta = metadata[Configuration.StandardMetadataXmlFieldName].EmbeddedValues[0];
                            if (standardMeta.ContainsKey(Configuration.StandardMetadataTitleXmlFieldName))
                            {
                                model.Title = standardMeta[Configuration.StandardMetadataTitleXmlFieldName].Value + postfix;
                            }

                            // determine description
                            if (standardMeta.ContainsKey(Configuration.StandardMetadataDescriptionXmlFieldName))
                            {
                                model.Meta.Add("description", standardMeta[Configuration.StandardMetadataDescriptionXmlFieldName].Value);
                            }
                        }
                        else if (fields.ContainsKey(Configuration.ComponentXmlFieldNameForPageTitle))
                        {
                            model.Title = fields[Configuration.ComponentXmlFieldNameForPageTitle].Value + postfix;
                        }
                    }
                }
                
                //Add header/footer
                IPage headerInclude = null;
                IPage footerInclude = null;
                if (includes != null)
                {
                    foreach (var include in includes)
                    {
                        var subPage = include as IPage;
                        if (subPage != null)
                        {
                            switch (subPage.Title)
                            {
                                case "Header":
                                    headerInclude = subPage;
                                    break;
                                case "Footer":
                                    footerInclude = subPage;
                                    break;
                            }
                        }
                    }
                }
                if (headerInclude != null)
                {
                    WebPage headerPage = (WebPage)Create(headerInclude, typeof(WebPage));
                    if (headerPage != null)
                    {
                        var header = new Header { Regions = new Dictionary<string, Region>() };
                        foreach (var region in headerPage.Regions)
                        {
                            header.Regions.Add(region.Key, region.Value);
                        }
                        model.Header = header;
                    }
                }
                if (footerInclude != null)
                {
                    WebPage footerPage = (WebPage)Create(footerInclude, typeof(WebPage));
                    if (footerPage != null)
                    {
                        var footer = new Footer { Regions = new Dictionary<string, Region>() };
                        foreach (var region in footerPage.Regions)
                        {
                            footer.Regions.Add(region.Key, region.Value);
                        }
                        model.Footer = footer;
                    }
                }
                return model;
            }
            throw new Exception(String.Format("Cannot create model for class {0}. Expecting IPage.", sourceEntity.GetType().FullName));

        }

        protected virtual Dictionary<string, string> ProcessPageMetadata(IPage page)
        {
            var meta = new Dictionary<string, string>();
            if (page.MetadataFields != null)
            {
                foreach (var field in page.MetadataFields.Values)
                {
                    ProcessMetadataField(field, meta);
                }
            }
            return meta;
        }

        protected virtual void ProcessMetadataField(IField field, Dictionary<string, string> meta)
        {
            if (field.FieldType==FieldType.Embedded)
            {
                if (field.EmbeddedValues!=null & field.EmbeddedValues.Count>0)
                {
                    var subfields = field.EmbeddedValues[0];
                    foreach (var subfield in subfields.Values)
                    {
                        ProcessMetadataField(subfield, meta);
                    }
                }
            }
            else
            {
                string value;
                switch (field.Name)
                {
                    case "internalLink":
                        value = LinkFactory.ResolveExtensionlessLink(field.Value);
                        break;
                    default:
                        value = String.Join(",", field.Values);
                        break;
                }
                if (value != null && !meta.ContainsKey(field.Name))
                {
                    meta.Add(field.Name, value);
                }
            }
        }



        private string GetResource(string name)
        {
            if (_resourceProvider == null)
            {
                _resourceProvider = new ResourceProvider();
            }
            return _resourceProvider.GetObject(name, CultureInfo.CurrentUICulture).ToString();
        }

        private static string GetRegionFromComponentPresentation(IComponentPresentation cp)
        {
            var match = Regex.Match(cp.ComponentTemplate.Title, @".*?\[(.*?)\]");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            //default region name
            return "Main";
        }

    }
}
