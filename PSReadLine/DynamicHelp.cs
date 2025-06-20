/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using Microsoft.PowerShell.Internal;
using Microsoft.PowerShell.PSReadLine;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        // Stub helper methods so dynamic help can be mocked
        [ExcludeFromCodeCoverage]
        void IPSConsoleReadLineMockableMethods.RenderFullHelp(string content, string regexPatternToScrollTo)
        {
            if (Options.UseCustomPager)
            {
                string regexPlaceholder = "<regex>";
                string arguments;

                // Both the CustomPagerArguments option and the regexPatternToScrollTo variable
                // need to contain text in order for the arguments to function properly. Confirmed
                // this with less.exe.
                if (!string.IsNullOrEmpty(Options.CustomPagerArguments) && !string.IsNullOrEmpty(regexPatternToScrollTo))
                {
                    arguments = Options.CustomPagerArguments.Replace(regexPlaceholder, regexPatternToScrollTo);
                }
                else
                {
                    arguments = "";
                }

                string temp = Path.GetTempFileName();
                File.WriteAllText(temp, content);

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = Options.CustomPagerCommand,
                    Arguments = arguments + $" \"{temp}\"",
                    UseShellExecute = false
                };
                try
                {
                    using Process process = Process.Start(psi);
                    process?.WaitForExit();
                    File.Delete(temp);
                }
                catch (Exception ex)
                {
                    _pager ??= new Pager();
                    _pager.Write(content, regexPatternToScrollTo);
                    throw new CustomPagerException(ex, Options.CustomPagerCommand);
                }
            }
            else
            {
                _pager ??= new Pager();
                _pager.Write(content, regexPatternToScrollTo);
            }
        }

        class CustomPagerException : Exception
        {
            public string CustomPagerCommand;
            internal CustomPagerException(Exception innerException, string customPagerCommand) : base("", innerException)
            {
                CustomPagerCommand = customPagerCommand;
            }
        }

        [ExcludeFromCodeCoverage]
        object IPSConsoleReadLineMockableMethods.GetDynamicHelpContent(string commandName, string parameterName, bool isFullHelp)
        {
            if (string.IsNullOrEmpty(commandName))
            {
                return null;
            }

            System.Management.Automation.PowerShell ps = null;

            try
            {
                if (!_mockableMethods.RunspaceIsRemote(_runspace))
                {
                    ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace);
                }
                else
                {
                    ps = System.Management.Automation.PowerShell.Create();
                    ps.Runspace = _runspace;
                }

                if (isFullHelp)
                {
                    return ps
                        .AddCommand($"Microsoft.PowerShell.Core\\Get-Help")
                        .AddParameter("Name", commandName)
                        .AddParameter("Full", value: true)
                        .AddCommand($"Microsoft.PowerShell.Utility\\Out-String")
                        .Invoke<string>()
                        .FirstOrDefault();
                }

                if (string.IsNullOrEmpty(parameterName))
                {
                    return null;
                }

                return ps
                    .AddCommand("Microsoft.PowerShell.Core\\Get-Help")
                    .AddParameter("Name", commandName)
                    .AddParameter("Parameter", parameterName)
                    .Invoke<PSObject>()
                    .FirstOrDefault();

            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                ps?.Dispose();

                // GetDynamicHelpContent could scroll the screen, e.g. via Write-Progress. For example,
                // Get-Help for unknown command under the CloudShell Azure drive will show the progress bar while searching for command.
                // We need to update the _initialY in case the current cursor postion has changed.
                if (_singleton._initialY > _console.CursorTop)
                {
                    _singleton._initialY = _console.CursorTop;
                }
            }
        }

        private Pager _pager;

        /// <summary>
        /// Attempt to show help content.
        /// Show the full help for the command on the alternate screen buffer.
        /// </summary>
        public static void ShowCommandHelp(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._console is PlatformWindows.LegacyWin32Console)
            {
                Collection<string> helpBlock = new Collection<string>()
                {
                    string.Empty,
                    PSReadLineResources.FullHelpNotSupportedInLegacyConsole
                };

                _singleton.WriteDynamicHelpBlock(helpBlock);

                return;
            }

            _singleton.DynamicHelpImpl(isFullHelp: true);
        }

        /// <summary>
        /// Attempt to show help content.
        /// Show the short help of the parameter next to the cursor.
        /// </summary>
        public static void ShowParameterHelp(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.DynamicHelpImpl(isFullHelp: false);
        }

        private void WriteDynamicHelpContent(string commandName, string parameterName, bool isFullHelp)
        {
            var helpContent = _mockableMethods.GetDynamicHelpContent(commandName, parameterName, isFullHelp);

            if (helpContent is string fullHelp && fullHelp.Length > 0)
            {
                string regexPatternToScrollTo = null;

                if (!string.IsNullOrEmpty(parameterName))
                {
                    // Original regex ($"-{parameterName} [<|\\[]") would sometimes match parameter in syntax line.
                    // New regex ($"^\\s*-{parameterName} [<|\\[]") only matches if it's at the beginning of the line.
                    regexPatternToScrollTo = $"^\\s*-{parameterName} [<|\\[]";
                }

                _mockableMethods.RenderFullHelp(fullHelp, regexPatternToScrollTo);
            }
            else if (helpContent is PSObject paramHelp)
            {
                WriteParameterHelp(paramHelp);
            }
        }

        private void DynamicHelpImpl(bool isFullHelp)
        {
            int cursor = _singleton._current;
            string commandName = null;
            string parameterName = null;

            // Simply return if nothing is rendered yet.
            if (_singleton._tokens == null) { return; }

            foreach(var token in _singleton._tokens)
            {
                var extent = token.Extent;

                if (extent.StartOffset > cursor)
                {
                    break;
                }

                if (token.TokenFlags == TokenFlags.CommandName)
                {
                    commandName = token.Text;
                }

                if (extent.StartOffset <= cursor && extent.EndOffset >= cursor)
                {
                    if (token.Kind == TokenKind.Parameter)
                    {
                        parameterName = ((ParameterToken)token).ParameterName;
                        break;
                    }
                }
            }

            WriteDynamicHelpContent(commandName, parameterName, isFullHelp);
        }

        private void WriteDynamicHelpBlock(Collection<string> helpBlock)
        {
            var dynHelp = new MultilineDisplayBlock
            {
                Singleton = this,
                ItemsToDisplay = helpBlock
            };

            dynHelp.DrawMultilineBlock();
            ReadKey();
            dynHelp.Clear();
        }

        private void WriteParameterHelp(dynamic helpContent)
        {
            Collection<string> helpBlock;

            if (helpContent?.Description is not string descriptionText)
            {
                descriptionText = helpContent?.Description?[0]?.Text;
            }

            if (descriptionText is null)
            {
                helpBlock = new Collection<string>()
                {
                    string.Empty,
                    PSReadLineResources.NeedsUpdateHelp
                };
            }
            else
            {
                string syntax = $"-{helpContent.name} <{helpContent.type.name}>";
                helpBlock = new Collection<string>
                {
                    string.Empty,
                    syntax,
                    string.Empty
                };

                string text = descriptionText;
                if (text.Contains("\n"))
                {
                    string[] lines = text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string prefix = i == 0 ? "DESC: " : "      ";
                        string s = prefix + lines[i].Trim('\r');
                        helpBlock.Add(s);
                    }
                }
                else
                {
                    string desc = "DESC: " + text;
                    // trim new line characters as some help content has it at the end of the first list on the description.
                    helpBlock.Add(desc.Trim('\r', '\n'));
                }

                string details = $"Required: {helpContent.required}, Position: {helpContent.position}, Default Value: {helpContent.defaultValue}, Pipeline Input: {helpContent.pipelineInput}, WildCard: {helpContent.globbing}";
                helpBlock.Add(details);
            }

            WriteDynamicHelpBlock(helpBlock);
        }

        private class MultilineDisplayBlock : DisplayBlockBase
        {
            internal Collection<string> ItemsToDisplay;

            // Keep track of the number of extra physical lines due to multi-line text.
            private int extraPhysicalLines = 0;

            public void DrawMultilineBlock()
            {
                IConsole console = Singleton._console;

                extraPhysicalLines = 0;

                SaveCursor();
                MoveCursorToStartDrawingPosition(console);

                var bufferWidth = console.BufferWidth;
                var items = ItemsToDisplay;

                for (var index = 0; index < items.Count; index++)
                {
                    var itemLength = LengthInBufferCells(items[index]);

                    int extra = 0;
                    if (itemLength > bufferWidth)
                    {
                        extra = itemLength / bufferWidth;
                        if (itemLength % bufferWidth == 0)
                        {
                            extra--;
                        }
                    }

                    if (extra > 0)
                    {
                        // Extra physical lines may cause buffer to scroll up.
                        AdjustForPossibleScroll(extra);
                        extraPhysicalLines += extra;
                    }

                    console.Write(items[index]);

                    // Explicit newline so consoles see each row as distinct lines, but skip the
                    // last line so we don't scroll.
                    if (index != (items.Count - 1))
                    {
                        AdjustForPossibleScroll(1);
                        MoveCursorDown(1);
                    }
                }

                RestoreCursor();
            }

            public void Clear()
            {
                _singleton.WriteBlankLines(Top, ItemsToDisplay.Count + extraPhysicalLines);
            }
        }
    }
}
