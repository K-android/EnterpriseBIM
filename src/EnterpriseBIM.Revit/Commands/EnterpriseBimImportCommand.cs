using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EnterpriseBIM.Core.Contracts;
using EnterpriseBIM.Core.Serialization;
using EnterpriseBIM.Revit.Failures;
using EnterpriseBIM.Revit.Reconstruction;

namespace EnterpriseBIM.Revit.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class EnterpriseBimImportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ArgumentNullException.ThrowIfNull(commandData);

            UIDocument? uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc is null || uiDoc.Document is null)
            {
                message = "An active Revit project document is required to run EnterpriseBIM Import.";
                return Result.Failed;
            }

            Document doc = uiDoc.Document;
            if (doc.IsReadOnly)
            {
                message = "Active Revit document is read-only.";
                return Result.Failed;
            }

            ExchangePackage package;
            try
            {
                package = ExchangeStreamSerializer.ReadPackageSynchronously();
            }
            catch (Exception ex)
            {
                message = $"Failed reading local EnterpriseBIM exchange payload: {ex.Message}";
                ExchangeStreamSerializer.AppendAuditLog("EnterpriseBIM.Revit", "ERROR", message);
                return Result.Failed;
            }

            if (package.Elements.IsEmpty)
            {
                ExchangeStreamSerializer.AppendAuditLog("EnterpriseBIM.Revit", "INFO", "Exchange package contained 0 elements.");
                return Result.Cancelled;
            }

            RevitElementReconstructor reconstructor;
            try
            {
                reconstructor = new RevitElementReconstructor(doc, package.UnitSystem);
            }
            catch (Exception ex)
            {
                message = $"Reconstruction engine initialization failed: {ex.Message}";
                ExchangeStreamSerializer.AppendAuditLog("EnterpriseBIM.Revit", "ERROR", message);
                return Result.Failed;
            }

            int succeededElements = 0;
            int failedElements = 0;

            using TransactionGroup rootGroup = new(doc, $"EnterpriseBIM Import [{package.PackageId:N}]");
            TransactionStatus groupStartStatus = rootGroup.Start();
            if (groupStartStatus != TransactionStatus.Started)
            {
                message = "Could not start root Revit TransactionGroup.";
                return Result.Failed;
            }

            try
            {
                foreach (BimElementPayload elementPayload in package.Elements)
                {
                    using Transaction elementTransaction = new(doc, $"Construct {elementPayload.SourceObjectId:N}");
                    if (elementTransaction.Start() != TransactionStatus.Started)
                    {
                        failedElements++;
                        continue;
                    }

                    FailureHandlingOptions failureOptions = elementTransaction.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new EnterpriseFailurePreProcessor(elementPayload.SourceObjectId));
                    failureOptions.SetClearAfterRollback(true);
                    failureOptions.SetForcedModalHandling(false);
                    elementTransaction.SetFailureHandlingOptions(failureOptions);

                    using SubTransaction elementSubTransaction = new(doc);
                    try
                    {
                        if (elementSubTransaction.Start() != TransactionStatus.Started)
                        {
                            throw new InvalidOperationException("Failed to start isolated SubTransaction.");
                        }

                        IReadOnlyList<ElementId> createdIds = reconstructor.ReconstructPayload(elementPayload);
                        if (createdIds.Count == 0)
                        {
                            throw new InvalidOperationException("Element reconstruction returned zero ElementIds.");
                        }

                        TransactionStatus subCommitStatus = elementSubTransaction.Commit();
                        if (subCommitStatus != TransactionStatus.Committed)
                        {
                            throw new InvalidOperationException($"SubTransaction commit returned status '{subCommitStatus}'.");
                        }

                        TransactionStatus txCommitStatus = elementTransaction.Commit();
                        if (txCommitStatus == TransactionStatus.Committed)
                        {
                            succeededElements++;
                        }
                        else
                        {
                            failedElements++;
                            ExchangeStreamSerializer.AppendAuditLog(
                                "EnterpriseBIM.Revit",
                                "WARN",
                                $"Element '{elementPayload.SourceObjectId}' rolled back by FailurePreprocessor (Status={txCommitStatus}).");
                        }
                    }
                    catch (Exception ex)
                    {
                        failedElements++;
                        if (elementSubTransaction.HasStarted())
                        {
                            elementSubTransaction.RollBack();
                        }
                        if (elementTransaction.HasStarted())
                        {
                            elementTransaction.RollBack();
                        }

                        ExchangeStreamSerializer.AppendAuditLog(
                            "EnterpriseBIM.Revit",
                            "ERROR",
                            $"Element reconstruction exception [SourceId={elementPayload.SourceObjectId}, Class={elementPayload.Classification}]: {ex.Message}");
                    }
                }

                if (succeededElements > 0)
                {
                    rootGroup.Assimilate();
                    ExchangeStreamSerializer.AppendAuditLog(
                        "EnterpriseBIM.Revit",
                        "INFO",
                        $"Import completed. PackageId={package.PackageId}, Succeeded={succeededElements}, Failed={failedElements}.");
                    return Result.Succeeded;
                }

                rootGroup.RollBack();
                message = $"All {failedElements} element(s) failed geometric validation. See '%programdata%\\EnterpriseBIM\\Logs' for details.";
                return Result.Failed;
            }
            catch (Exception fatalEx)
            {
                if (rootGroup.HasStarted())
                {
                    rootGroup.RollBack();
                }
                message = $"Fatal transaction group exception: {fatalEx.Message}";
                ExchangeStreamSerializer.AppendAuditLog("EnterpriseBIM.Revit", "FATAL", fatalEx.ToString());
                return Result.Failed;
            }
        }
    }
}
