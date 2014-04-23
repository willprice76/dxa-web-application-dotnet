﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Compilation;
using System.Web.Helpers;
using System.Web.Script.Serialization;

namespace Sdl.Web.Mvc
{
    /// <summary>
    /// Resource Provider to read resources from JSON files on disk
    /// </summary>
    public class ResourceProvider : IResourceProvider
    {
        private static Dictionary<string, Dictionary<string, object>> _resources;
        private static object resourceLock = new object();
        public object GetObject(string resourceKey, System.Globalization.CultureInfo culture)
        {
            //Ignore the culture - we read this from the RequestContext
            var dictionary = GetResourceCache();
            if (!dictionary.Contains(resourceKey))
            {
                //default is to return resource key, to aid troubleshooting
                return resourceKey;
            }
            return dictionary[resourceKey];
        }

        public System.Resources.IResourceReader ResourceReader
        {
            get { return new ResourceReader(GetResourceCache()); }
        }

        private IDictionary GetResourceCache()
        {
            return Resources(WebRequestContext.Localization.Path);
        }

        public IDictionary Resources(string localization)
        {
            if (_resources == null)
            {
                LoadResources();
            }
            if (!_resources.ContainsKey(localization))
            {
                throw new Exception(String.Format("No resources can be found for localization {0}. Check that the localization path is correct and the resources have been published.",localization));
            }
            return _resources[localization];
        }

        private void LoadResources()
        {
            //We are reading into a static variable, so need to be thread safe
            lock (resourceLock)
            {
                var applicationRoot = AppDomain.CurrentDomain.BaseDirectory;
                _resources = new Dictionary<string, Dictionary<string, object>>();
                foreach (var loc in Configuration.Localizations.Values)
                {
                    //Just in case the same localization is in there more than once
                    if (!_resources.ContainsKey(loc.Path))
                    {
                        var resources = new Dictionary<string, object>();
                        var path = String.Format("{0}{1}/{2}", applicationRoot, loc.Path, Configuration.AddVersionToPath(Configuration.SYSTEM_FOLDER + "/resources/_all.json"));
                        if (File.Exists(path))
                        {
                            //The _all.json file contains a list of all other resources files to load
                            var bootstrapJson = Json.Decode(File.ReadAllText(path));
                            foreach (string file in bootstrapJson.files)
                            {
                                var type = file.Substring(file.LastIndexOf("/") + 1);
                                type = type.Substring(0, type.LastIndexOf(".")).ToLower();
                                var filePath = applicationRoot + Configuration.AddVersionToPath(file);
                                if (File.Exists(filePath))
                                {
                                    foreach (var item in GetResourcesFromFile(filePath))
                                    {
                                        //we ensure resource key uniqueness by adding the type (which comes from the filename)
                                        resources.Add(String.Format("{0}.{1}", type, item.Key), item.Value);
                                    }
                                }
                                else
                                {
                                    //TODO log a warning
                                }
                            }
                            _resources.Add(loc.Path, resources);
                        }
                        else
                        {
                            //TODO Log a warning - although this should not be an error - 
                            //its quite possible that localizations are configured for which resources are not yet available.
                        }
                    }
                }
            }     
        }

        private Dictionary<string, object> GetResourcesFromFile(string filePath)
        {
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(filePath));
        }
    }

    internal sealed class ResourceReader : IResourceReader
    {
        private readonly IDictionary _dictionary;

        public ResourceReader(IDictionary resources)
        {
            _dictionary = resources;
        }

        public void Close() { }

        public IDictionaryEnumerator GetEnumerator()
        {
            return _dictionary.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _dictionary.GetEnumerator();
        }

        public void Dispose() { }
    }
}
