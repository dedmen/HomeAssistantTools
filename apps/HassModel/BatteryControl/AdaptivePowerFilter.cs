using System;
using System.Collections.Generic;
using System.Text;

namespace HomeAssistantNetDaemon.apps.HassModel.BatteryControl
{
    internal class AdaptivePowerFilter
    {
        private readonly float _alphaSlow = 0.15f; // For normal, small fluctuations
        private readonly float _alphaFast = 0.80f; // For medium transitions

        private float _smoothedValue;
        private readonly float _bypassThreshold;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="initialValue"></param>
        /// <param name="bypassThreshold">Power fluctuations greater than this many watts, bypass the filter and get passed straight through</param>
        public AdaptivePowerFilter(float initialValue = 0.0f, float bypassThreshold = 500.0f)
        {
            _smoothedValue = initialValue;
            _bypassThreshold = bypassThreshold;
        }


        public float Filter(float powerReading)
        {
            var delta = Math.Abs(powerReading - _smoothedValue);

            if (delta > _bypassThreshold)
            {
                // Sudden change (Oven turned on), instantly jump to new value. No delay
                _smoothedValue = powerReading;
            }
            else
            {
                // Small fluctuation, smooth it out

                var ratio = delta / _bypassThreshold;  // Scale to 0-1
                var alpha = _alphaSlow + (_alphaFast - _alphaSlow) * ratio;

                // Exponential Moving Average
                _smoothedValue = (alpha * powerReading) + (
                    (1.0f - alpha) * _smoothedValue
                );

            }

            return _smoothedValue;
        }
    }
}
