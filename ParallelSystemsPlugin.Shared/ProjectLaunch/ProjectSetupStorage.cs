// Created by Jhay
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace ParallelSystemsPlugin.ProjectLaunch
{
    internal static class ProjectSetupStorage
    {
        private static readonly Guid SchemaId = new Guid("9bca1054-8655-4c43-9af8-d7607fd2a111");
        private const string SchemaName = "ParallelSystemsProjectSetup011";

        public static ProjectSetupState Load(Document document)
        {
            var state = new ProjectSetupState();
            if (document?.ProjectInformation == null)
                return state;

            Schema schema = Schema.Lookup(SchemaId);
            if (schema == null)
                return state;
            ValidateSchema(schema);

            Entity entity = document.ProjectInformation.GetEntity(schema);
            if (!entity.IsValid())
                return state;

            state.SchemaVersion = Get(entity, schema, "SchemaVersion", 1);
            state.ClientNamingConvention = Get(entity, schema, "ClientNamingConvention", "");
            state.NamingConventionConfirmed = Get(entity, schema, "NamingConventionConfirmed", false);
            state.ProjectManager = Get(entity, schema, "ProjectManager", "");
            state.ProjectOverseer = Get(entity, schema, "ProjectOverseer", "");
            state.GridLevelWorksetName = Get(entity, schema, "GridLevelWorksetName", "Shared Levels and Grids");
            state.InitialModelWorksetName = Get(entity, schema, "InitialModelWorksetName", "Workset1");
            state.AdditionalWorksetNames = GetArray(entity, schema, "AdditionalWorksetNames");
            state.RequiredLinkNames = GetArray(entity, schema, "RequiredLinkNames");
            state.NoLinksRequired = Get(entity, schema, "NoLinksRequired", false);
            state.CoordinationSourceLinkName = Get(entity, schema, "CoordinationSourceLinkName", "");
            state.CoordinationReviewed = Get(entity, schema, "CoordinationReviewed", false);
            state.CopyMonitorNotRequired = Get(entity, schema, "CopyMonitorNotRequired", false);
            state.CopyMonitorNotRequiredReason = Get(entity, schema, "CopyMonitorNotRequiredReason", "");
            state.SetupCompleted = Get(entity, schema, "SetupCompleted", false);
            state.CompletedBy = Get(entity, schema, "CompletedBy", "");
            state.CompletedUtc = Get(entity, schema, "CompletedUtc", "");
            state.CompletedPluginVersion = Get(entity, schema, "CompletedPluginVersion", "");
            return state;
        }

        public static void Save(Document document, ProjectSetupState state)
        {
            if (document?.ProjectInformation == null)
                throw new InvalidOperationException("Project Information is unavailable.");

            Schema schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            Set(entity, schema, "SchemaVersion", 1);
            Set(entity, schema, "ClientNamingConvention", state.ClientNamingConvention ?? "");
            Set(entity, schema, "NamingConventionConfirmed", state.NamingConventionConfirmed);
            Set(entity, schema, "ProjectManager", state.ProjectManager ?? "");
            Set(entity, schema, "ProjectOverseer", state.ProjectOverseer ?? "");
            Set(entity, schema, "GridLevelWorksetName", state.GridLevelWorksetName ?? "");
            Set(entity, schema, "InitialModelWorksetName", state.InitialModelWorksetName ?? "");
            SetArray(entity, schema, "AdditionalWorksetNames", state.AdditionalWorksetNames);
            SetArray(entity, schema, "RequiredLinkNames", state.RequiredLinkNames);
            Set(entity, schema, "NoLinksRequired", state.NoLinksRequired);
            Set(entity, schema, "CoordinationSourceLinkName", state.CoordinationSourceLinkName ?? "");
            Set(entity, schema, "CoordinationReviewed", state.CoordinationReviewed);
            Set(entity, schema, "CopyMonitorNotRequired", state.CopyMonitorNotRequired);
            Set(entity, schema, "CopyMonitorNotRequiredReason", state.CopyMonitorNotRequiredReason ?? "");
            Set(entity, schema, "SetupCompleted", state.SetupCompleted);
            Set(entity, schema, "CompletedBy", state.CompletedBy ?? "");
            Set(entity, schema, "CompletedUtc", state.CompletedUtc ?? "");
            Set(entity, schema, "CompletedPluginVersion", state.CompletedPluginVersion ?? "");
            document.ProjectInformation.SetEntity(entity);
        }

        private static Schema GetOrCreateSchema()
        {
            Schema existing = Schema.Lookup(SchemaId);
            if (existing != null)
            {
                ValidateSchema(existing);
                return existing;
            }

            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetVendorId("PSYS");
            builder.AddSimpleField("SchemaVersion", typeof(int));
            AddString(builder, "ClientNamingConvention");
            builder.AddSimpleField("NamingConventionConfirmed", typeof(bool));
            AddString(builder, "ProjectManager"); AddString(builder, "ProjectOverseer");
            AddString(builder, "GridLevelWorksetName"); AddString(builder, "InitialModelWorksetName");
            builder.AddArrayField("AdditionalWorksetNames", typeof(string));
            builder.AddArrayField("RequiredLinkNames", typeof(string));
            builder.AddSimpleField("NoLinksRequired", typeof(bool));
            AddString(builder, "CoordinationSourceLinkName");
            builder.AddSimpleField("CoordinationReviewed", typeof(bool));
            builder.AddSimpleField("CopyMonitorNotRequired", typeof(bool));
            AddString(builder, "CopyMonitorNotRequiredReason");
            builder.AddSimpleField("SetupCompleted", typeof(bool));
            AddString(builder, "CompletedBy"); AddString(builder, "CompletedUtc");
            AddString(builder, "CompletedPluginVersion");
            return builder.Finish();
        }

        private static void ValidateSchema(Schema schema)
        {
            if (schema.SchemaName != SchemaName || schema.GetField("SchemaVersion") == null ||
                schema.GetField("ClientNamingConvention") == null || schema.GetField("SetupCompleted") == null)
            {
                throw new InvalidOperationException("The Project Setup schema GUID is already used by an unknown schema. No project data was changed.");
            }
        }

        private static void AddString(SchemaBuilder builder, string name) => builder.AddSimpleField(name, typeof(string));
        private static T Get<T>(Entity entity, Schema schema, string name, T fallback)
        {
            Field field = schema.GetField(name);
            return field == null ? fallback : entity.Get<T>(field);
        }
        private static List<string> GetArray(Entity entity, Schema schema, string name)
        {
            Field field = schema.GetField(name);
            return field == null ? new List<string>() : new List<string>(entity.Get<IList<string>>(field));
        }
        private static void Set<T>(Entity entity, Schema schema, string name, T value) => entity.Set(schema.GetField(name), value);
        private static void SetArray(Entity entity, Schema schema, string name, IList<string> values) =>
            entity.Set(schema.GetField(name), values ?? new List<string>());
    }
}
