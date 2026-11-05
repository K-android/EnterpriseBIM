using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using EnterpriseBIM.Core.Serialization;

namespace EnterpriseBIM.Revit.Failures
{
    public sealed class EnterpriseFailurePreProcessor : IFailuresPreprocessor
    {
        private readonly Guid _sourceObjectId;

        public EnterpriseFailurePreProcessor(Guid sourceObjectId)
        {
            _sourceObjectId = sourceObjectId;
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            ArgumentNullException.ThrowIfNull(failuresAccessor);

            IList<FailureMessageAccessor> failureMessages = failuresAccessor.GetFailureMessages();
            if (failureMessages.Count == 0)
            {
                return FailureProcessingResult.Continue;
            }

            bool requiresRollBack = false;

            foreach (FailureMessageAccessor failure in failureMessages)
            {
                FailureSeverity severity = failure.GetSeverity();
                FailureDefinitionId failureId = failure.GetFailureDefinitionId();
                string description = failure.GetDescriptionText();

                if (severity == FailureSeverity.Warning)
                {
                    ExchangeStreamSerializer.AppendAuditLog(
                        "EnterpriseBIM.Revit.FailurePreprocessor",
                        "WARN",
                        $"Suppressed Revit Warning [SourceId={_sourceObjectId}, DefinitionId={failureId.Guid}]: {description}");

                    failuresAccessor.DeleteWarning(failure);
                    continue;
                }

                if (severity == FailureSeverity.Error)
                {
                    if (failuresAccessor.IsFailureResolutionPermitted(failure))
                    {
                        FailureResolutionType defaultResolution = failure.GetDefaultResolutionType();
                        if (defaultResolution != FailureResolutionType.Invalid &&
                            failure.HasResolutionOfType(defaultResolution))
                        {
                            ExchangeStreamSerializer.AppendAuditLog(
                                "EnterpriseBIM.Revit.FailurePreprocessor",
                                "WARN",
                                $"Auto-resolved Revit Error [SourceId={_sourceObjectId}, Resolution={defaultResolution}]: {description}");

                            failuresAccessor.ResolveFailure(failure);
                            return FailureProcessingResult.ProceedWithCommit;
                        }
                    }

                    ExchangeStreamSerializer.AppendAuditLog(
                        "EnterpriseBIM.Revit.FailurePreprocessor",
                        "ERROR",
                        $"Unresolvable Revit Error encountered [SourceId={_sourceObjectId}]: {description}");

                    requiresRollBack = true;
                }
            }

            if (requiresRollBack)
            {
                FailureHandlingOptions options = failuresAccessor.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                failuresAccessor.SetFailureHandlingOptions(options);
                return FailureProcessingResult.ProceedWithRollBack;
            }

            return FailureProcessingResult.Continue;
        }
    }
}
