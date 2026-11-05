using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using EnterpriseBIM.Core.Contracts;
using EnterpriseBIM.Core.Enums;
using EnterpriseBIM.Core.Serialization;
using EnterpriseBIM.Rhino.Factories;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace EnterpriseBIM.Rhino.Commands
{
    public sealed class EnterpriseBimHarvestCommand : Command
    {
        public static EnterpriseBimHarvestCommand? Instance { get; private set; }

        public EnterpriseBimHarvestCommand()
        {
            Instance = this;
        }

        public override string EnglishName => "EnterpriseBIM_Harvest";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc is null)
            {
                return Result.Failure;
            }

            using GetObject go = new();
            go.SetCommandPrompt("Select Curves, Breps, or Extrusions for EnterpriseBIM local exchange harvest");
            go.GeometryFilter = ObjectType.Curve | ObjectType.Brep | ObjectType.Extrusion;
            go.SubObjectSelect = false;
            go.GroupSelect = true;
            go.EnablePreSelect(true, ignoreUnacceptablePreselectedObjects: true);

            GetResult getResult = go.GetMultiple(minimumNumber: 1, maximumNumber: 0);
            if (getResult != GetResult.Object)
            {
                RhinoApp.WriteLine("[EnterpriseBIM] Harvest cancelled or no valid geometry selected.");
                return Result.Cancel;
            }

            double scaleFactor = RhinoGeometryTranslator.GetRhinoToSupportedUnitScale(doc.ModelUnitSystem, out LengthUnitSystem mappedUnits);

            ImmutableArray<BimElementPayload>.Builder payloadBuilder = ImmutableArray.CreateBuilder<BimElementPayload>(go.ObjectCount);
            int skippedCount = 0;

            for (int i = 0; i < go.ObjectCount; i++)
            {
                ObjRef objRef = go.Object(i);
                RhinoObject? rhinoObj = objRef.Object();
                if (rhinoObj is null)
                {
                    skippedCount++;
                    continue;
                }

                try
                {
                    BimElementPayload? elementPayload = RhinoGeometryTranslator.TranslateRhinoObject(rhinoObj, doc, scaleFactor);
                    if (elementPayload is not null)
                    {
                        payloadBuilder.Add(elementPayload);
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
                catch (Exception ex)
                {
                    skippedCount++;
                    ExchangeStreamSerializer.AppendAuditLog(
                        "EnterpriseBIM.Rhino",
                        "WARN",
                        $"Failed translating RhinoObject '{rhinoObj.Id}': {ex.Message}");
                }
            }

            if (payloadBuilder.Count == 0)
            {
                RhinoApp.WriteLine("[EnterpriseBIM] No valid BIM payloads could be extracted from selection.");
                return Result.Nothing;
            }

            ExchangePackage package = new()
            {
                PackageId = Guid.NewGuid(),
                SchemaVersion = ExchangePackage.CurrentSchemaVersion,
                ExportedAtUtc = DateTimeOffset.UtcNow,
                SourceApplication = $"Rhinoceros {RhinoApp.Version}",
                SourceDocumentPath = string.IsNullOrWhiteSpace(doc.Path) ? "UnsavedDocument.3dm" : doc.Path,
                AuthorDomainUser = $"{Environment.UserDomainName}\\{Environment.UserName}",
                UnitSystem = mappedUnits,
                Elements = payloadBuilder.ToImmutable()
            };

            string stagingPath = ExchangeStreamSerializer.GetEnterpriseStagingFilePath();

            try
            {
                Task.Run(async () =>
                {
                    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(45));
                    await ExchangeStreamSerializer.WritePackageAsync(package, stagingPath, cts.Token).ConfigureAwait(false);
                }).GetAwaiter().GetResult();

                ExchangeStreamSerializer.AppendAuditLog(
                    "EnterpriseBIM.Rhino",
                    "INFO",
                    $"Harvested {payloadBuilder.Count} elements (Skipped: {skippedCount}) into '{stagingPath}'. PackageId={package.PackageId}");

                RhinoApp.WriteLine($"[EnterpriseBIM] Successfully harvested {payloadBuilder.Count} elements to '{stagingPath}'.");
                return Result.Success;
            }
            catch (Exception ex)
            {
                ExchangeStreamSerializer.AppendAuditLog(
                    "EnterpriseBIM.Rhino",
                    "ERROR",
                    $"Exchange package write failure: {ex}");
                RhinoApp.WriteLine($"[EnterpriseBIM] Critical failure writing exchange file: {ex.Message}");
                return Result.Failure;
            }
        }
    }
}
