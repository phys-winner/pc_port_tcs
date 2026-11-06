namespace ClosingShift.Android
{
    internal static class AndroidNativeMousePolicy
    {
        internal static float Filter(string name, float value, bool android, bool mousePresent, int touchCount)
        {
            bool mouseAxis = name == "Mouse X" || name == "Mouse Y" || name == "Mouse ScrollWheel";
            return android && mouseAxis && (!mousePresent || touchCount != 0) ? 0 : value;
        }
    }
}
