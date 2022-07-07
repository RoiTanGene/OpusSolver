using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Opus.Solution;
using Opus.UI.Analysis;
using static Opus.KeyboardUtils;
using static System.FormattableString;

namespace Opus.UI.Rendering
{
    public class ProgramRenderer
    {
        private static readonly log4net.ILog sm_log = log4net.LogManager.GetLogger(typeof(ProgramRenderer));

        private ProgramGrid m_grid;
        private Program m_program;
        private List<Arm> m_arms;

        private InstructionRenderer m_instructionRenderer;
        private int m_renderDelay = 10;

        private List<Instruction> lastCopy;
        private bool lastRowErrored = false;

        public ProgramRenderer(ProgramGrid grid, Program program, IEnumerable<Arm> arms)
        {
            m_grid = grid;
            m_program = program;
            m_arms = arms.ToList();

            m_instructionRenderer = new InstructionRenderer(grid);
        }

        public void Render()
        {
            sm_log.Info("Rendering program");

            m_grid.SetMaxY(m_arms.Count);

            int width = m_grid.GetNumVisibleCells().X;
            int maxTime = m_program.Instructions.Values.Max(x => x.Count());
            for (int time = 0; time <= maxTime; time += width)
            {
                RenderPage(time, width);
            }
        }

        private void RenderPage(int startTime, int width)
        {
            sm_log.Info(Invariant($"Rendering page; startTime: {startTime}; width: {width}"));
            for (int armIndex = 0; armIndex < m_arms.Count; armIndex++)
            {
                var instructions = m_program.GetArmInstructions(m_arms[armIndex]);
                int endTime = Math.Min(instructions.Count, startTime + width) - 1;
                if (startTime <= endTime)
                {
                    RenderRow(startTime, endTime, armIndex, instructions);
                }
            }
        }

        private void RenderRow(int startTime, int endTime, int armIndex, List<Instruction> instructions)
        {
            sm_log.Info(Invariant($"Rendering arm {armIndex} from {startTime} to {endTime}"));
            m_grid.EnsureCellVisible(new Vector2(startTime, armIndex));

            int chainCopy = 0;

            for (int timeIndex = startTime; timeIndex <= endTime; timeIndex++)
            {
                if (!instructions[timeIndex].IsRenderable())
                {
                    continue;
                }

                // It's quite common for an arm to have similar instructions to the previous arm, so
                // try to copy them if possible. 
                int numToCopy = FindCopyableInstructions(timeIndex, endTime, armIndex, out var copyList);

                // Don't bother for single instructions as it's quicker to just recreate them
                if (numToCopy > 1)
                {
                    m_grid.EnsureCellsVisible(new Vector2(startTime, armIndex - 1), new Vector2(numToCopy, 2));
                    CopyInstructionsFromPrevious(timeIndex, numToCopy, armIndex, out chainCopy, copyList);

                    if (chainCopy > 0)
                    {
                        chainCopy = numToCopy;
                    }
                    timeIndex += numToCopy - 1;
                }
                else
                {
                    lastCopy = null;
                    m_instructionRenderer.Render(new Vector2(timeIndex, armIndex), instructions[timeIndex], m_renderDelay);
                }
            }

            EnsureRowCorrect(startTime, endTime, armIndex, instructions, chainCopy);
        }

        /// <summary>
        /// Finds the number of instructions that are the same between the specified arm and the
        /// previous arm, starting at startTime.
        /// </summary>
        private int FindCopyableInstructions(int startTime, int endTime, int armIndex, out List<Instruction> copyList)
        {
            copyList = null;
            int result = 0;
            if (armIndex == 0)
            {
                return result;
            }

            var instructions = m_program.GetArmInstructions(m_arms[armIndex]);
            var prevInstructions = m_program.GetArmInstructions(m_arms[armIndex - 1]);

            int time;
            for (time = startTime; time <= endTime; time++)
            {
                if (time >= instructions.Count() || time >= prevInstructions.Count() || instructions[time] != prevInstructions[time])
                {
                    break;
                }
            }

            int lastSame = time - 1;
            if (lastSame > 0)
            {
                // Exclude non-renderable instructions at the end of the sequence
                int lastRenderable = instructions.FindLastIndex(lastSame, lastSame - startTime + 1, i => i.IsRenderable());
                if (lastRenderable < 0)
                {
                    return result;
                }
                else
                {
                    result = lastRenderable - startTime + 1;
                    copyList = instructions.GetRange(startTime, result);
                    return result;
                }
            }

            result = lastSame - startTime + 1;
            copyList = instructions.GetRange(startTime, result);
            return result;
        }

        private void CopyInstructionsFromPrevious(int timeIndex, int width, int armIndex, out int chainCopy, List<Instruction> copyList = null)
        {
            chainCopy = 0;

            // Select the instructions to copy
            var sourcePos = new Vector2(timeIndex, armIndex - 1);
            var dragEnd = m_grid.GetCellLocation(sourcePos);

            if (lastCopy == null || copyList == null || !copyList.SequenceEqual(lastCopy))
            {
                var dragStart = m_grid.GetCellLocation(new Vector2(timeIndex + width - 1, armIndex));
                MouseUtils.LeftDrag(dragStart, dragEnd);
                lastCopy = copyList;
            }
            else
            {
                chainCopy = 1;
            }

            // Copy them
            KeyDown(Keys.ControlKey);
            ThreadUtils.SleepOrAbort(m_renderDelay);
            var copyEnd = m_grid.GetCellLocation(new Vector2(timeIndex, armIndex));
            MouseUtils.LeftDrag(dragEnd, copyEnd, m_renderDelay);

            KeyUp(Keys.ControlKey);
            ThreadUtils.SleepOrAbort(m_renderDelay);
        }

        private void EnsureRowCorrect(int startTime, int endTime, int armIndex, List<Instruction> instructions, int chainCopy = 0)
        {
            const int maxRetries = 5;
            int retryCount = 0;
            int? prevErrors = null;

            var errors = FindErrors(startTime, endTime, armIndex, instructions);
            while (errors.Any())
            {
                lastCopy = null;
                if (lastRowErrored)
                {
                    retryCount = 1;
                }

                if (prevErrors.HasValue && errors.Count() < prevErrors.Value)
                {
                    // The number of errors has decreased, so reset the retry count
                    retryCount = 1;
                }
                else
                {
                    // We haven't made any progress, so increment the retry count
                    retryCount++;
                    if (retryCount > maxRetries)
                    {
                        throw new RenderException(Invariant($"{errors.Count()} instructions for arm {armIndex} between time {startTime} and {endTime} are still incorrect after {retryCount} attempts."));
                    }
                }

                if (retryCount > 1)
                {
                    // First retry did not work, increase render delay
                    m_renderDelay += 10;
                    sm_log.Info("Increasing render delay to " + m_renderDelay);
                }

                foreach (int timeIndex in errors)
                {
                    sm_log.Info(Invariant($"Re-rendering instruction for arm {armIndex} at time {timeIndex}"));
                    if (chainCopy > 0)
                    {
                        m_grid.EnsureCellsVisible(new Vector2(startTime, armIndex - 1), new Vector2(chainCopy, 2));
                        CopyInstructionsFromPrevious(timeIndex, chainCopy, armIndex, out chainCopy);
                    }
                    else
                    {
                        m_instructionRenderer.Render(new Vector2(timeIndex, armIndex), instructions[timeIndex], m_renderDelay);
                    }
                }

                errors = FindErrors(startTime, endTime, armIndex, instructions);
            }
            if (retryCount == 0)
            {
                lastRowErrored = false;
            }
        }

        private IEnumerable<int> FindErrors(int startTime, int endTime, int armIndex, List<Instruction> instructions)
        {
            var errors = FindErrors().ToList();
            if (errors.Any())
            {
                sm_log.Warn(Invariant($"{errors.Count()} instructions for arm {armIndex} between time {startTime} and {endTime} are incorrect. Waiting a little while before checking again..."));

                ThreadUtils.SleepOrAbort(100 + m_renderDelay);
                errors = FindErrors().ToList();
                if (errors.Any())
                {
                    sm_log.Warn(Invariant($"{errors.Count()} instructions for arm {armIndex} between time {startTime} and {endTime} are still incorrect."));
                }
            }

            return errors;

            IEnumerable<int> FindErrors()
            {
                var rect = m_grid.GetCellScreenBounds(new Bounds(new Vector2(startTime, armIndex), new Vector2(endTime, armIndex)));
                using (var capture = new ScreenCapture(rect))
                {
                    var analyzer = new InstructionAnalyzer(capture);
                    for (int timeIndex = startTime; timeIndex <= endTime; timeIndex++)
                    {
                        var instruction = instructions[timeIndex];
                        if (instruction.IsRenderable())
                        {
                            var location = m_grid.GetCellLocation(new Vector2(timeIndex, armIndex)).Subtract(rect.Location);
                            if (!analyzer.IsMatch(location, instructions[timeIndex]))
                            {
                                yield return timeIndex;
                            }
                        }
                    }
                }
            }
        }
    }
}
