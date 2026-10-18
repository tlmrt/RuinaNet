namespace RuinaNet
{
    // Independent of Unity so packet timing and phase transitions can be tested.
    internal sealed class RoundGate
    {
        public int Round = -1;
        public bool Armed;
        public bool Handled;
        private int _planningEpoch = -1;

        public void Reset()
        {
            Round = -1;
            Armed = Handled = false;
            _planningEpoch = -1;
        }

        public void BeginPlanning(int gameRound)
        {
            if (_planningEpoch == gameRound) return;
            _planningEpoch = gameRound;
            Armed = Handled = false;
        }

        public bool TryArm(bool inPlanning, int requested)
        {
            if (!inPlanning || Armed || Handled) return false;
            if (requested >= 0 && requested != Round + 1) return false;
            Round++;
            Armed = Handled = true;
            return true;
        }

        public bool TryRelease(bool inPlanning)
        {
            if (!Armed || !inPlanning) return false;
            Armed = false;
            // Only a NEW game round may clear Handled. Network traffic cannot.
            return true;
        }
    }
}
