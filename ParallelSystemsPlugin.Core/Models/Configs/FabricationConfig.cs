namespace ParallelSystemsPlugin.Models.Configs
{
    // Created by Jhay: central DN-based fabrication fit-up defaults that can
    // be adjusted to the applicable project/WPS decision.
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

            double setback = nominalDiameterMillimetres <= 125
                ? SlipOnPipeFaceSetbackDn125AndBelowMillimetres
                : SlipOnPipeFaceSetbackDn150AndAboveMillimetres;

            if (setback <= 0.0)
            {
                throw new System.InvalidOperationException(
                    "Slip-on pipe-face setback configuration must be " +
                    "greater than zero millimetres.");
            }

            return setback;
        }
    }
}
