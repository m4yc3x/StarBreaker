﻿using CliFx;
using StarBreaker.Chf;
using StarBreaker.Cli;
using StarBreaker.Cli.DataCoreCommands;
using StarBreaker.Cli.P4kCommands;

return await new CliApplicationBuilder()
    .AddCommand<DownloadCommand>()
    .AddCommand<ExportAllCommand>()
    .AddCommand<ImportAllCommand>()
    .AddCommand<ProcessAllCommand>()
    .AddCommand<ProcessCommand>()
    .AddCommand<WatchExportCommand>()
    .AddCommand<WatchImportCommand>()
    .AddCommand<DataCoreExtractCommand>()
    .AddCommand<DataCoreExtractSingleCommand>()
    .AddCommand<ExtractP4kCommand>()
    .AddCommand<ExtractProtobufsCommand>()
    .AddCommand<ExtractDescriptorSetCommand>()
    .Build()
    .RunAsync();