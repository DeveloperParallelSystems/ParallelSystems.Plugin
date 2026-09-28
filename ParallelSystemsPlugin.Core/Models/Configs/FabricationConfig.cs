namespace ParallelSystemsPlugin.Models.Configs
{
    // Created by Jhay: central fabrication fit-up values that must come from
    // the applicable project/WPS decision rather than dimensional standards.
    public class FabricationConfig
    {
        // Created by Jhay: populated dynamic defaults for plate/SOW flange
        // fit-up. Each flange resolves its own band from nominal diameter.
        public double SlipOnPipeFaceSetbackDn125AndBelowMillimetres
        {
            get;
            set;
        } = 6.0;

        public double SlipOnPipeFaceSetbackDn150AndAboveMillimetres
        {
            get;
            set;
        } = 10.0;

        public double ResolveSlipOnPipeFaceSetbackMillimetres(
            int nominalDiameterMillimetres)
        {
            if (nominalDiameterMillimetres <= 0)
            {
                throw new System.ArgumentOutOfRangeException(
                    nameof(nominalDiameterMillimetres));
            }

            return nominalDiameterMillimetres <= 125
                ? SlipOnPipeFaceSetbackDn125AndBelowMillimetres
                : SlipOnPipeFaceSetbackDn150AndAboveMillimetres;
        }

        // Retained temporarily so previously saved configuration files can
        // still be read while the UI migrates to the two dynamic bands.
        public double? SlipOnPipeFaceSetbackMillimetres { get; set; }
    }
}
