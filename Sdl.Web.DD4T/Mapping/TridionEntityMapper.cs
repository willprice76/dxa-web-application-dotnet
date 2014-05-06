﻿using System;
using System.Collections.Generic;
using DD4T.ContentModel;
using Sdl.Web.Mvc.Mapping;

namespace Sdl.Web.DD4T.Mapping
{
    public class TridionEntityMapper : IEntityMapper
    {
        // TODO probabaly need a method based on a List<SemanticProperty>, quite similar but with vocab name instead of prefix, so use SemanticMapping.GetVocabulary(prefix)
        public object GetPropertyValue(object sourceEntity, List<FieldSemantics> semantics)
        {
            IComponent component = ((IComponentPresentation)sourceEntity).Component;

            // tcm:0-1
            string[] uriParts = component.Schema.Id.Split('-');
            long schemaId = Convert.ToInt64(uriParts[1]);

            // get semantic mappings for fields from schema
            SemanticSchema schema = SemanticMapping.GetSchema(schemaId);

            foreach (var semanticProperty in semantics)
            {
                // find schema field that matches "Prefix" = semanticProperty.Prefix && "Entity" = semanticProperty.Entity && "Property" = semanticProperty.Property
                var matchingField = schema.FindFieldByProperty(semanticProperty);
                if (matchingField != null && component.Fields.ContainsKey(matchingField.Name))
                {
                    IField field = component.Fields[matchingField.Name];

                    // TODO return correct index from possible multiple values
                    switch (field.FieldType)
                    {
                        case FieldType.Number:
                            return field.NumericValues[0];
                        case FieldType.Date:
                            return field.DateTimeValues[0];
                        case FieldType.ComponentLink:
                            return field.LinkedComponentValues[0];
                        case FieldType.Keyword:
                            return field.Keywords[0];
                        default:
                            return field.Value;
                    }
                }
            }

            return null;
        }
    }
}
