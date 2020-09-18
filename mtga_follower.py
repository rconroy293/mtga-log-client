import importlib.util
import pathlib

if __name__ == "__main__":
    path = pathlib.Path(__file__).parent.absolute().joinpath("mtga_follower", "seventeenlands")

    spec = importlib.util.spec_from_file_location("seventeenlands.mtga_follower", path.joinpath("mtga_follower.py"))

    mtga_folower = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mtga_folower)

    mtga_folower.main()
