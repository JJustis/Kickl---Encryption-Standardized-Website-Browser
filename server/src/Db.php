<?php

declare(strict_types=1);

namespace Kickl\Registry;

use PDO;

final class Db
{
    public static function connect(bool $withDatabase = true): PDO
    {
        Config::boot();
        $dsn = sprintf(
            'mysql:host=%s;port=%d;charset=utf8mb4',
            Config::dbHost(),
            Config::dbPort()
        );
        if ($withDatabase) {
            $dsn .= ';dbname=' . Config::dbName();
        }

        return new PDO($dsn, Config::dbUser(), Config::dbPassword(), [
            PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
            PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
            PDO::ATTR_EMULATE_PREPARES => false,
        ]);
    }
}
