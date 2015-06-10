﻿namespace Sdl.Web.Common.Models
{
    [SemanticEntity(Vocab = SchemaOrgVocabulary, EntityName = "MediaObject", Prefix = "s", Public = true)]
    public class MediaItem : EntityModel
    {
        [SemanticProperty("s:contentUrl")]
        public string Url { get; set; }
        public string FileName { get; set; }
        [SemanticProperty("s:contentSize")]
        public int FileSize { get; set; }
        public string MimeType { get; set; }
    }
}