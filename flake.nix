{
  description = "Truck-kun Tower: a two-player tower defense game";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { nixpkgs, ... }:
    let
      inherit (nixpkgs) lib;
      name = "truck-kun-tower";
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      forAllSystems = f: lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      packages = forAllSystems (pkgs: {
        default = pkgs.buildDotnetModule {
          pname = name;
          version = "0.1.0";
          src = lib.fileset.toSource {
            root = ./.;
            fileset = lib.fileset.unions [
              (./. + "/${name}.csproj")
              ./Program.cs
              ./Duel.cs
              ./Maze.cs
              ./wwwroot
            ];
          };
          projectFile = "${name}.csproj";
          dotnet-sdk = pkgs.dotnet-sdk_8;
          dotnet-runtime = pkgs.dotnet-aspnetcore_8;
          executables = [ name ];
          meta = {
            description = "Two-player tower defense game";
            mainProgram = name;
            platforms = systems;
          };
        };
      });

      formatter = forAllSystems (pkgs: pkgs.nixfmt-tree);
    };
}
