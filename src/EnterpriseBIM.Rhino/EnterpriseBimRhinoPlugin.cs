using System;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

[assembly: PlugInDescription(DescriptionType.Address, "Enterprise Architecture Platform")]
[assembly: PlugInDescription(DescriptionType.Country, "US")]
[assembly: PlugInDescription(DescriptionType.Email, "bim-devops@enterprise.internal")]
[assembly: PlugInDescription(DescriptionType.Organization, "Enterprise AEC Interoperability Division")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://bim.enterprise.internal")]
[assembly: Guid("9C4F8D21-7A1E-4B63-8E51-3D29C84A10F2")]

namespace EnterpriseBIM.Rhino
{
    public sealed class EnterpriseBimRhinoPlugin : PlugIn
    {
        public static EnterpriseBimRhinoPlugin? Instance { get; private set; }

        public EnterpriseBimRhinoPlugin()
        {
            Instance = this;
        }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;
    }
}
