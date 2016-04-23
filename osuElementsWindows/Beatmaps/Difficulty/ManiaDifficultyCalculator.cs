﻿namespace osuElements.Beatmaps.Difficulty
{
    public class ManiaDifficultyCalculator : DifficultyCalculatorBase
    {
        public override GameMode GameMode => GameMode.Mania;
        public override double StarDifficulty { get; set; }
        public override void Calculate(Mods mods) {
            base.Calculate(mods);
            throw new System.NotImplementedException();
        }

        public override double PerformancePoints(ushort count300, ushort count100, ushort count50, ushort countMiss, bool scorev2) {
            throw new System.NotImplementedException();
        }
    }
}